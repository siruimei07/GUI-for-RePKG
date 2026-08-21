using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallpaperField.Models;
using WallpaperField.Services;

internal static class TransactionRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        await RejectExistingUpdateFailureAsync("first", "unpacked/a.bin", assert);
        await RejectExistingUpdateFailureAsync("middle", "unpacked/b.bin", assert);
        await RejectExistingUpdateFailureAsync("final metadata", "metadata.json", assert);
        await RejectNewOutputMetadataConflictAsync(assert);
        await IgnoreTransactionWorkingMetadataAsync(assert);
    }

    private static async Task RejectExistingUpdateFailureAsync(
        string caseName,
        string lockedRelativePath,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Transaction-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "8001");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "8001");
        var unpackedRoot = Path.Combine(itemRoot, "unpacked");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            Directory.CreateDirectory(unpackedRoot);
            File.WriteAllText(Path.Combine(unpackedRoot, "A.bin"), "old-a", Encoding.UTF8);
            File.WriteAllText(Path.Combine(unpackedRoot, "b.bin"), "old-b", Encoding.UTF8);
            File.WriteAllText(Path.Combine(unpackedRoot, "c.bin"), "old-c", Encoding.UTF8);
            File.WriteAllText(Path.Combine(unpackedRoot, "sentinel.keep"), "unrelated", Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(unpackedRoot, ".wallpaper-field-unpack.json"),
                "old-manifest",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(itemRoot, "metadata.json"), "old-metadata", Encoding.UTF8);
            WritePackage(
                packagePath,
                [
                    ("a.bin", Encoding.UTF8.GetBytes("new-a")),
                    ("b.bin", Encoding.UTF8.GetBytes("new-b")),
                    ("c.bin", Encoding.UTF8.GetBytes("new-c"))
                ]);

            var before = SnapshotTree(itemRoot);
            WallpaperUnpackResult result;
            using (new FileStream(
                       Path.Combine(itemRoot, lockedRelativePath),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                result = await new RePkgWallpaperUnpackService().UnpackAsync(
                    new WallpaperUnpackRequest
                    {
                        OutputDirectory = outputRoot,
                        Items =
                        [
                            new WallpaperRecord
                            {
                                WorkshopId = "8001",
                                Title = "Transactional update",
                                SourceDirectory = sourceDirectory,
                                OutputDirectory = itemRoot,
                                HasScenePackage = true,
                                ScenePackagePath = packagePath
                            }
                        ]
                    });
            }

            var after = SnapshotTree(itemRoot);
            assert(result.FailedCount == 1, $"A locked {caseName} destination must fail the item.");
            assert(
                result.CommittedCount == 0
                && result.UnchangedFailureCount == 1
                && result.AdditionalEffectsPossibleCount == 0
                && result.Errors.Single().CommitState == WallpaperItemCommitState.NotModified
                && result.ItemResults.Single() is
                {
                    Outcome: WallpaperUnpackOutcome.Failed,
                    CommitState: WallpaperItemCommitState.NotModified,
                    CompletedWork: 15,
                    WorkUnit: WallpaperWorkUnit.Bytes
                },
                $"A locked {caseName} destination must report an unchanged failure.");
            assert(
                before.SequenceEqual(after),
                $"A {caseName} commit failure must leave the existing item tree byte-for-byte unchanged.");
            assert(
                !HasWorkingDirectory(itemRoot),
                $"A {caseName} commit failure must not leave staging or backup directories.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task RejectNewOutputMetadataConflictAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Transaction-New-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "8002");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "8002");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            Directory.CreateDirectory(Path.Combine(itemRoot, "metadata.json"));
            WritePackage(packagePath, [("a.bin", Encoding.UTF8.GetBytes("new-a"))]);
            var before = SnapshotTree(itemRoot);

            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "8002",
                            Title = "New metadata conflict",
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = itemRoot,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                });

            assert(
                result.FailedCount == 1
                && result.UnchangedFailureCount == 1
                && result.Errors.Single().CommitState == WallpaperItemCommitState.NotModified,
                "A new-output metadata type conflict must report an unchanged failure.");
            assert(
                before.SequenceEqual(SnapshotTree(itemRoot)),
                "A new-output metadata conflict must leave the pre-existing tree unchanged.");
            assert(
                !HasWorkingDirectory(itemRoot),
                "A new-output metadata conflict must not leave staging or backup directories.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task IgnoreTransactionWorkingMetadataAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Transaction-Library-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "8100");

        try
        {
            WriteMetadata(itemRoot, "8100", "Committed item");
            WriteMetadata(
                Path.Combine(itemRoot, ".wallpaper-field-stage-fixture"),
                "stage-phantom",
                "Staged phantom");
            WriteMetadata(
                Path.Combine(itemRoot, ".wallpaper-field-backup-fixture"),
                "backup-phantom",
                "Backup phantom");

            var result = await new WallpaperLibraryService().LoadAsync(outputRoot);
            assert(
                result.Items.Select(item => item.WorkshopId).SequenceEqual(["8100"]),
                "Library discovery must ignore transaction staging and backup metadata.");
            assert(
                result.Errors.Count == 0,
                "Ignored transaction working metadata must not create duplicate or parse errors.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void WriteMetadata(
        string directory,
        string workshopId,
        string title)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "metadata.json"),
            JsonSerializer.Serialize(
                new WallpaperRecord
                {
                    WorkshopId = workshopId,
                    Title = title,
                    OutputDirectory = directory
                }),
            Encoding.UTF8);
    }

    private static bool HasWorkingDirectory(string itemRoot)
        => Directory.EnumerateDirectories(itemRoot)
            .Any(path => Path.GetFileName(path).StartsWith(
                             ".wallpaper-field-stage-",
                             StringComparison.Ordinal)
                         || Path.GetFileName(path).StartsWith(
                             ".wallpaper-field-backup-",
                             StringComparison.Ordinal));

    private static IReadOnlyList<string> SnapshotTree(string root)
    {
        var snapshot = new List<string>();
        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            snapshot.Add($"D|{Path.GetRelativePath(root, directory)}");
        }

        foreach (var file in Directory
                     .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            snapshot.Add(
                $"F|{Path.GetRelativePath(root, file)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}");
        }

        return snapshot;
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
