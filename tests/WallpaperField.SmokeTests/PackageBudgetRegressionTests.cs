using System.IO;
using System.Text;
using WallpaperField.Services;
using WallpaperField.ThirdParty.RePKG;

internal static class PackageBudgetRegressionTests
{
    private const int PathDepthLimit = 64;
    private const int AggregatePathByteLimit = 16 * 1024 * 1024;

    internal static void Run(Action<bool, string> assert)
    {
        RejectDeepPathsBeforePlanning(assert);
        AcceptDepthBoundary(assert);
        CheckAggregateReaderBudget(assert);
        RejectAggregatePlannerInput(assert);
        RejectAggregatePlanningExpansion(assert);
        PreserveCollisionChecks(assert);
        ReuseSharedAncestors(assert);
        AcceptOrdinaryArchive(assert);
    }

    private static void RejectDeepPathsBeforePlanning(Action<bool, string> assert)
    {
        foreach (var separator in new[] { '/', '\\' })
        {
            var path = string.Join(separator, Enumerable.Repeat("d", PathDepthLimit)) + separator + "file.bin";
            using var stream = WritePackage([path]);
            ExpectInvalidData(() => SafePackageReader.Read(stream), "depth", assert);
            assert(
                stream.Position == 16 + sizeof(int) + Encoding.UTF8.GetByteCount(path),
                "An over-depth PKG path must be rejected before reading its body range.");

            var root = NewStagingRoot();
            ExpectInvalidData(
                () => PackageExtractionPlanner.Build(Package([path]), root),
                "depth",
                assert);
            assert(!Directory.Exists(root), "Deep-path rejection must not create output directories.");
        }
    }

    private static void AcceptDepthBoundary(Action<bool, string> assert)
    {
        var path = string.Join('/', Enumerable.Repeat("d", PathDepthLimit - 1)) + "/file.bin";
        using var stream = WritePackage([path]);
        var parsed = SafePackageReader.Read(stream);
        var plan = PackageExtractionPlanner.Build(parsed, NewStagingRoot());
        assert(plan.Entries.Count == 1, "A PKG path at the supported depth boundary must be accepted.");
        assert(plan.PhysicalByteCount == 0, "Zero-body entries remain valid at the depth boundary.");
    }

    private static void CheckAggregateReaderBudget(Action<bool, string> assert)
    {
        // Two-byte UTF-8 exercises byte accounting rather than UTF-16 character counting.
        var path = new string('\u00e9', 2048);
        var entryCount = AggregatePathByteLimit / Encoding.UTF8.GetByteCount(path);
        using (var stream = WritePackage(Enumerable.Repeat(path, entryCount)))
        {
            var parsed = SafePackageReader.Read(stream);
            assert(parsed.Entries.Count == entryCount, "Aggregate path bytes exactly at the limit must be accepted.");
        }

        using var oversized = WritePackage(Enumerable.Repeat(path, entryCount).Append("x"));
        ExpectInvalidData(() => SafePackageReader.Read(oversized), "aggregate", assert);
        assert(
            oversized.Position == oversized.Length - 1 - 2 * sizeof(int),
            "The aggregate metadata limit must reject the next path before reading its bytes or data range.");
    }

    private static void RejectAggregatePlannerInput(Action<bool, string> assert)
    {
        var path = new string('\u00e9', 2048);
        var count = AggregatePathByteLimit / Encoding.UTF8.GetByteCount(path);
        var package = Package(Enumerable.Repeat(path, count).Append("x"));
        ExpectInvalidData(
            () => PackageExtractionPlanner.Build(package, NewStagingRoot()),
            "aggregate",
            assert);
    }

    private static void RejectAggregatePlanningExpansion(Action<bool, string> assert)
    {
        // About 780 KiB of metadata, with bounded depth, would retain roughly
        // 100 MiB of ancestor strings without a separate planning budget.
        var suffix = string.Join('/', Enumerable.Repeat(new string('a', 48), PathDepthLimit - 2));
        var paths = Enumerable.Range(0, 256).Select(index => $"entry{index:D4}/{suffix}/file.bin");
        var root = NewStagingRoot();
        ExpectInvalidData(
            () => PackageExtractionPlanner.Build(Package(paths), root),
            "planning",
            assert);
        assert(!Directory.Exists(root), "Planning-budget rejection must precede output creation.");
    }

    private static void PreserveCollisionChecks(Action<bool, string> assert)
    {
        string[][] cases =
        [
            ["A.txt", "a.TXT"],
            ["shared/a.bin", "SHARED"],
            ["SHARED", "shared/a.bin"],
            ["shared/a.bin", "shared/deeper/b.bin", "SHARED/DEEPER"],
            ["shared/a.bin", "SHARED/deeper", "shared/deeper/b.bin"],
            ["materials/foo.tex", "MATERIALS/FOO.PNG"],
            ["materials/foo.png/child.bin", "materials/foo.tex"],
            ["materials/foo.tex", "materials/FOO.PNG/child.bin"]
        ];

        foreach (var paths in cases)
        {
            ExpectInvalidData(
                () => PackageExtractionPlanner.Build(Package(paths), NewStagingRoot()),
                "冲突",
                assert);
        }
    }

    private static void ReuseSharedAncestors(Action<bool, string> assert)
    {
        var prefix = string.Join('/', Enumerable.Range(0, PathDepthLimit - 1).Select(index => $"d{index:D2}"));
        var package = Package(Enumerable.Range(0, 1024).Select(index => $"{prefix}/file{index:D4}.bin"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var plan = PackageExtractionPlanner.Build(package, NewStagingRoot());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        assert(plan.Entries.Count == 1024, "Entries sharing deep ancestors must remain valid.");
        assert(
            allocated < 32L * 1024 * 1024,
            $"Known ancestors must be reused without repeatedly allocating every prefix; allocated {allocated:N0} bytes.");
    }

    private static void AcceptOrdinaryArchive(Action<bool, string> assert)
    {
        var paths = Enumerable.Range(0, 10_000).Select(index => $"materials/group{index % 100:D2}/file{index:D5}.bin");
        using var stream = WritePackage(paths);
        var parsed = SafePackageReader.Read(stream);
        var plan = PackageExtractionPlanner.Build(parsed, NewStagingRoot());
        assert(plan.Entries.Count == 10_000, "A large, shallow archive must remain supported.");
        assert(plan.AllowedFinalRelativePaths.Count == 10_002, "Ordinary files and both metadata files must be planned.");
    }

    private static SafePackage Package(IEnumerable<string> paths)
        => new("PKGV0024", 0, paths.Select(path => new SafePackageEntry(path, 0, 0)).ToArray());

    private static string NewStagingRoot()
        => Path.Combine(Path.GetTempPath(), $"WF-PkgBudget-{Guid.NewGuid():N}", "unpacked");

    private static FileStream WritePackage(IEnumerable<string> paths)
    {
        var entries = paths.ToArray();
        var stream = new FileStream(
            Path.Combine(Path.GetTempPath(), $"WF-PkgBudget-{Guid.NewGuid():N}.pkg"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(8);
        writer.Write(Encoding.UTF8.GetBytes("PKGV0024"));
        writer.Write(entries.Length);
        foreach (var path in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write(0);
            writer.Write(0);
        }
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static void ExpectInvalidData(Action action, string messageFragment, Action<bool, string> assert)
    {
        try
        {
            action();
            assert(false, $"Expected InvalidDataException containing '{messageFragment}'.");
        }
        catch (InvalidDataException exception)
        {
            assert(
                exception.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase),
                $"Expected rejection containing '{messageFragment}', received: {exception.Message[..Math.Min(200, exception.Message.Length)]}");
        }
    }
}
