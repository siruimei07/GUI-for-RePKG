using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.Services;

internal static class ProjectBrowserPreviewRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        VerifySurfaceAndLimits(assert);
        await VerifyRealDecoderAsync(assert);
        await VerifyInFlightAndConcurrencyAsync(assert);
        await VerifyCacheAndLeaseLifecycleAsync(assert);
        await VerifyFailureRetryAndGenerationAsync(assert);
        await VerifyViewportControlAsync(assert);
        await VerifyBrowseIntegrationSurfaceAsync(assert);
    }

    private static void VerifySurfaceAndLimits(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperRecord).Assembly;
        var requiredTypes = new[]
        {
            "WallpaperField.Models.PreviewThumbnailLimits",
            "WallpaperField.Models.PreviewThumbnailRequest",
            "WallpaperField.Models.PreviewThumbnailResult",
            "WallpaperField.Services.IPreviewThumbnailDecoder",
            "WallpaperField.Services.WpfPreviewThumbnailDecoder",
            "WallpaperField.Services.PreviewThumbnailService",
            "WallpaperField.Controls.ThumbnailPreviewImage"
        };
        var missing = requiredTypes
            .Where(typeName => assembly.GetType(typeName, false, false) is null)
            .ToArray();

        assert(
            missing.Length == 0,
            "Project browser preview contract is missing: " + string.Join(", ", missing));

        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumInputBytes),
            64L * 1024 * 1024,
            assert);
        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumDimension),
            4096,
            assert);
        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumSourcePixels),
            16L * 1024 * 1024,
            assert);
        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumConcurrentDecodes),
            4,
            assert);
        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumEntries),
            128,
            assert);
        VerifyConstant(
            typeof(PreviewThumbnailLimits),
            nameof(PreviewThumbnailLimits.MaximumDecodedCacheBytes),
            128L * 1024 * 1024,
            assert);

        assert(
            PreviewThumbnailLimits.GetSizeBucket(193) == 256
            && PreviewThumbnailLimits.GetSizeBucket(255) == 256
            && PreviewThumbnailLimits.GetSizeBucket(4097) == 4096,
            "Preview target widths do not normalize to stable, bounded cache buckets.");

        var exactPixelOverflowRejected = false;
        try
        {
            PreviewThumbnailLimits.ValidateSourcePixelCount(
                PreviewThumbnailLimits.MaximumSourcePixels + 1);
        }
        catch (PreviewThumbnailBudgetException exception)
        {
            exactPixelOverflowRejected = exception.Code == "PREVIEW_SOURCE_PIXELS";
        }

        assert(exactPixelOverflowRejected,
            "The exact source-pixel limit + 1 was not rejected before allocation.");
    }

    private static async Task VerifyRealDecoderAsync(Action<bool, string> assert)
    {
        using var fixture = new PreviewFixture();
        var pngPath = fixture.WriteImage("preview.png", new PngBitmapEncoder(), 512, 320);
        var jpegPath = fixture.WriteImage("preview.jpg", new JpegBitmapEncoder(), 320, 200);
        var gifPath = fixture.WriteGif(
            "preview.gif",
            CreateFrozenBitmap(16, 10, blue: 0, green: 0, red: 255),
            CreateFrozenBitmap(16, 10, blue: 255, green: 0, red: 0));
        var workerThreadIds = new ConcurrentQueue<int>();
        var decoder = new WpfPreviewThumbnailDecoder(workerThreadIds.Enqueue);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var results = await Task.WhenAll(
            decoder.DecodeAsync(CreateDecodeRequest(pngPath, 193), CancellationToken.None),
            decoder.DecodeAsync(CreateDecodeRequest(jpegPath, 193), CancellationToken.None),
            decoder.DecodeAsync(CreateDecodeRequest(gifPath, 193), CancellationToken.None));
        Console.WriteLine(
            "PREVIEW_FORMAT_RESULTS "
            + string.Join(",", results.Select(result =>
                $"{result.Status}:{result.FailureCode ?? "READY"}")));

        assert(results.All(result => result.IsSuccess),
            "PNG/JPEG/GIF static thumbnail decoder did not return ready results.");
        assert(results.All(result => result.Bitmap?.IsFrozen == true),
            "A decoded project thumbnail was not frozen for cross-thread use.");
        assert(results.All(result => result.Bitmap!.PixelWidth <= 256),
            "A decoded project thumbnail ignored its target size bucket.");
        var gifPixel = ReadFirstBgraPixel(results[2].Bitmap!);
        assert(gifPixel.Red > gifPixel.Blue,
            "The GIF thumbnail did not retain the static first frame.");
        assert(workerThreadIds.Count == 3
               && workerThreadIds.All(threadId => threadId != callerThreadId),
            "Preview FileInfo/FileStream/BitmapDecoder work did not start on worker threads.");

        foreach (var path in new[] { pngPath, jpegPath, gifPath })
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            assert(exclusive.Length > 0,
                "The real WPF thumbnail decoder retained a preview file handle.");
        }

        var missingPath = fixture.WriteImage("missing.png", new PngBitmapEncoder(), 4, 4);
        var missingRequest = CreateDecodeRequest(missingPath, 128);
        File.Delete(missingPath);
        var missing = await decoder.DecodeAsync(missingRequest, CancellationToken.None);

        var corruptPath = fixture.WriteBytes("corrupt.png", [1, 2, 3, 4, 5, 6]);
        var corrupt = await decoder.DecodeAsync(
            CreateDecodeRequest(corruptPath, 128),
            CancellationToken.None);

        var changedPath = fixture.WriteImage("changed.png", new PngBitmapEncoder(), 4, 4);
        var changedFacts = CreateDecodeRequest(changedPath, 128);
        await using (var append = new FileStream(
                         changedPath,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None,
                         1,
                         FileOptions.Asynchronous))
        {
            await append.WriteAsync(new byte[] { 42 });
        }
        var changed = await decoder.DecodeAsync(changedFacts, CancellationToken.None);

        var timestampChangedPath = fixture.WriteImage(
            "timestamp-changed.png",
            new PngBitmapEncoder(),
            4,
            4);
        var timestampChangedFacts = CreateDecodeRequest(timestampChangedPath, 128);
        File.SetLastWriteTimeUtc(
            timestampChangedPath,
            timestampChangedFacts.ScanLastWriteTimeUtc.UtcDateTime.AddMinutes(1));
        var timestampChanged = await decoder.DecodeAsync(
            timestampChangedFacts,
            CancellationToken.None);

        var oversizedPath = fixture.CreateSparseFile(
            "oversized.png",
            PreviewThumbnailLimits.MaximumInputBytes + 1);
        var oversized = await decoder.DecodeAsync(
            CreateDecodeRequest(oversizedPath, 128),
            CancellationToken.None);

        var widePath = fixture.WriteImage(
            "too-wide.png",
            new PngBitmapEncoder(),
            PreviewThumbnailLimits.MaximumDimension + 1,
            1);
        var wide = await decoder.DecodeAsync(
            CreateDecodeRequest(widePath, 128),
            CancellationToken.None);

        assert(missing.Status == PreviewThumbnailStatus.Missing,
            "A missing preview did not become a stable missing result.");
        assert(corrupt.Status == PreviewThumbnailStatus.Corrupt,
            "A corrupt preview did not become a stable corrupt result.");
        assert(changed.Status == PreviewThumbnailStatus.Changed,
            "A preview whose length changed after scanning was decoded anyway.");
        assert(timestampChanged.Status == PreviewThumbnailStatus.Changed,
            "A preview whose timestamp changed after scanning was decoded anyway.");
        assert(oversized.Status == PreviewThumbnailStatus.OverBudget,
            "A 64 MiB + 1 preview was not rejected before decode.");
        assert(wide.Status == PreviewThumbnailStatus.OverBudget,
            "A 4097-pixel source dimension was not rejected before target decode.");

        var targetDirectory = fixture.CreateDirectory("junction-target");
        var targetPreview = fixture.WriteImage(
            Path.Combine("junction-target", "preview.png"),
            new PngBitmapEncoder(),
            4,
            4);
        var junctionPath = Path.Combine(fixture.Root, "preview-junction");
        CreateDirectoryJunction(junctionPath, targetDirectory);
        try
        {
            var linkedPreview = Path.Combine(junctionPath, Path.GetFileName(targetPreview));
            var reparse = await decoder.DecodeAsync(
                CreateDecodeRequest(linkedPreview, 128),
                CancellationToken.None);
            assert(reparse.Status == PreviewThumbnailStatus.ReparsePoint,
                "A preview reached through a reparse point was decoded.");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
        }
    }

    private static async Task VerifyInFlightAndConcurrencyAsync(Action<bool, string> assert)
    {
        var releaseShared = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedDecoder = new ControlledDecoder(async (_, token) =>
        {
            await releaseShared.Task.WaitAsync(token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(32, 20));
        });
        using (var service = new PreviewThumbnailService(sharedDecoder))
        {
            service.SetGeneration(1);
            var request = CreateFakeRequest(1, generation: 1);
            using var first = service.Acquire(request);
            using var second = service.Acquire(request);
            await WaitUntilAsync(() => sharedDecoder.CallCount == 1);
            assert(service.GetMetrics().ObserverCount == 2
                   && service.GetMetrics().InFlightShareCount == 1,
                "Same-key thumbnail observers did not share one in-flight decode.");
            releaseShared.TrySetResult(true);
            var results = await Task.WhenAll(first.Completion, second.Completion);
            assert(results.All(result => result.IsSuccess)
                   && ReferenceEquals(results[0].Bitmap, results[1].Bitmap),
                "Same-key leases did not receive the same frozen thumbnail instance.");
        }

        var keyedDecoder = new ControlledDecoder((_, _) => Task.FromResult(
            PreviewThumbnailResult.Ready(CreateFrozenBitmap(8, 8))));
        using (var service = new PreviewThumbnailService(keyedDecoder))
        {
            service.SetGeneration(11);
            var baseline = CreateFakeRequest(7, generation: 11);
            var caseVariant = new PreviewThumbnailRequest(
                baseline.ProjectKey,
                baseline.CanonicalPath.ToUpperInvariant(),
                baseline.ScanFileLength,
                baseline.ScanLastWriteTimeUtc,
                baseline.PreviewFormat,
                193,
                11);
            var lengthVariant = new PreviewThumbnailRequest(
                baseline.ProjectKey,
                baseline.CanonicalPath,
                baseline.ScanFileLength + 1,
                baseline.ScanLastWriteTimeUtc,
                baseline.PreviewFormat,
                193,
                11);
            var timestampVariant = new PreviewThumbnailRequest(
                baseline.ProjectKey,
                baseline.CanonicalPath,
                baseline.ScanFileLength,
                baseline.ScanLastWriteTimeUtc.AddTicks(1),
                baseline.PreviewFormat,
                193,
                11);
            var bucketVariant = new PreviewThumbnailRequest(
                baseline.ProjectKey,
                baseline.CanonicalPath,
                baseline.ScanFileLength,
                baseline.ScanLastWriteTimeUtc,
                baseline.PreviewFormat,
                321,
                11);

            foreach (var request in new[]
                     {
                         baseline,
                         caseVariant,
                         lengthVariant,
                         timestampVariant,
                         bucketVariant
                     })
            {
                using var lease = service.Acquire(request);
                _ = await lease.Completion;
            }

            assert(keyedDecoder.CallCount == 4,
                "Thumbnail cache identity did not normalize path casing or include scan length, mtime, and size bucket.");
        }

        var releaseMany = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrencyDecoder = new ControlledDecoder(async (_, token) =>
        {
            await releaseMany.Task.WaitAsync(token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(8, 8));
        });
        using (var service = new PreviewThumbnailService(concurrencyDecoder))
        {
            service.SetGeneration(2);
            var leases = Enumerable.Range(0, 12)
                .Select(index => service.Acquire(CreateFakeRequest(index, generation: 2)))
                .ToArray();
            try
            {
                await WaitUntilAsync(() => service.GetMetrics().ActiveDecodes == 4);
                await Task.Delay(50);
                var blockedMetrics = service.GetMetrics();
                PerformanceRegressionTests.ReportThumbnailMetrics(
                    "concurrency_gate",
                    blockedMetrics,
                    assert);
                assert(concurrencyDecoder.PeakActiveCount <= 4
                       && blockedMetrics.PeakActiveDecodes <= 4
                       && concurrencyDecoder.CallCount == 4
                       && blockedMetrics.PendingDecodes == 12,
                    "Thumbnail decode concurrency or pending metrics exceeded the four-worker gate.");
                releaseMany.TrySetResult(true);
                await Task.WhenAll(leases.Select(lease => lease.Completion));
                assert(concurrencyDecoder.CallCount == 12,
                    "The bounded decoder queue did not eventually serve every observed request.");
            }
            finally
            {
                foreach (var lease in leases)
                {
                    lease.Dispose();
                }
            }
        }
    }

    private static async Task VerifyCacheAndLeaseLifecycleAsync(Action<bool, string> assert)
    {
        var decoder = new ControlledDecoder((_, _) => Task.FromResult(
            PreviewThumbnailResult.Ready(CreateFrozenBitmap(1, 1))));
        using (var service = new PreviewThumbnailService(decoder))
        {
            service.SetGeneration(3);
            var requests = Enumerable.Range(0, PreviewThumbnailLimits.MaximumEntries + 12)
                .Select(index => CreateFakeRequest(index, generation: 3))
                .ToArray();
            foreach (var request in requests)
            {
                using var lease = service.Acquire(request);
                var result = await lease.Completion;
                assert(result.IsSuccess, "The fake cache fixture did not complete successfully.");
            }

            var metrics = service.GetMetrics();
            PerformanceRegressionTests.ReportThumbnailMetrics(
                "count_lru",
                metrics,
                assert);
            assert(metrics.CacheEntryCount == PreviewThumbnailLimits.MaximumEntries
                   && metrics.CacheDecodedBytes == PreviewThumbnailLimits.MaximumEntries * 4L
                   && metrics.EvictionCount == 12,
                "Thumbnail LRU did not enforce the exact entry-count budget.");

            var recentCalls = decoder.CallCount;
            using (var recent = service.Acquire(requests[^1]))
            {
                _ = await recent.Completion;
            }

            assert(decoder.CallCount == recentCalls
                   && service.GetMetrics().CacheHitCount > 0,
                "A recently used cached thumbnail was decoded again.");

            using (var oldest = service.Acquire(requests[0]))
            {
                _ = await oldest.Completion;
            }

            assert(decoder.CallCount == recentCalls + 1,
                "The least-recently-used thumbnail was not evicted under cache pressure.");
        }

        const long fakeDecodedBytes = 3L * 1024 * 1024;
        var byteDecoder = new ControlledDecoder((_, _) => Task.FromResult(
            new PreviewThumbnailResult(
                PreviewThumbnailStatus.Ready,
                CreateFrozenBitmap(1, 1),
                fakeDecodedBytes,
                null,
                null)));
        using (var service = new PreviewThumbnailService(byteDecoder))
        {
            service.SetGeneration(31);
            foreach (var index in Enumerable.Range(0, 50))
            {
                using var lease = service.Acquire(CreateFakeRequest(index, generation: 31));
                _ = await lease.Completion;
            }

            var metrics = service.GetMetrics();
            var expectedEntries = (int)(
                PreviewThumbnailLimits.MaximumDecodedCacheBytes / fakeDecodedBytes);
            PerformanceRegressionTests.ReportThumbnailMetrics(
                "byte_lru",
                metrics,
                assert);
            assert(metrics.CacheEntryCount == expectedEntries
                   && metrics.CacheDecodedBytes == expectedEntries * fakeDecodedBytes
                   && metrics.EvictionCount == 50 - expectedEntries,
                "Thumbnail LRU did not enforce the exact decoded-byte budget.");
        }

        var neverRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellableDecoder = new ControlledDecoder(async (_, token) =>
        {
            await neverRelease.Task.WaitAsync(token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        using (var service = new PreviewThumbnailService(cancellableDecoder))
        {
            service.SetGeneration(4);
            var activeLeases = Enumerable.Range(0, 4)
                .Select(index => service.Acquire(CreateFakeRequest(index, generation: 4)))
                .ToArray();
            await WaitUntilAsync(() => cancellableDecoder.CallCount == 4);
            var queuedLease = service.Acquire(CreateFakeRequest(4, generation: 4));
            await WaitUntilAsync(() => service.GetMetrics().PendingDecodes == 5);
            queuedLease.Dispose();
            var queuedResult = await queuedLease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            assert(queuedResult.Status == PreviewThumbnailStatus.Cancelled
                   && cancellableDecoder.CallCount == 4,
                "The last observer leaving a queued thumbnail did not cancel it.");

            foreach (var activeLease in activeLeases)
            {
                activeLease.Dispose();
            }

            var activeResults = await Task.WhenAll(
                    activeLeases.Select(lease => lease.Completion))
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => service.GetMetrics().PendingDecodes == 0);
            var metrics = service.GetMetrics();
            assert(activeResults.All(result => result.Status == PreviewThumbnailStatus.Cancelled)
                   && metrics.ObserverCount == 0
                   && metrics.PendingDecodes == 0
                   && metrics.CancellationCount == 5,
                "Last-observer cancellation left a decode, pending entry, or observer behind.");
        }
    }

    private static async Task VerifyFailureRetryAndGenerationAsync(Action<bool, string> assert)
    {
        var attempts = 0;
        var decoder = new ControlledDecoder((_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return Task.FromResult(attempt <= 2
                ? PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_CORRUPT",
                    "预览图损坏。")
                : PreviewThumbnailResult.Ready(CreateFrozenBitmap(6, 4)));
        });
        using (var service = new PreviewThumbnailService(decoder))
        {
            service.SetGeneration(5);
            var signals = new List<PreviewThumbnailSignalEventArgs>();
            service.StatusChanged += (_, args) => signals.Add(args);
            var request = CreateFakeRequest(1, generation: 5);

            using (var first = service.Acquire(request))
            {
                assert((await first.Completion).Status == PreviewThumbnailStatus.Corrupt,
                    "The fake stable failure was not returned.");
            }

            using (var cached = service.Acquire(request))
            {
                assert((await cached.Completion).Status == PreviewThumbnailStatus.Corrupt,
                    "A stable failure was not served from cache.");
            }

            await WaitUntilAsync(() => signals.Count > 0);
            assert(decoder.CallCount == 1
                   && signals.Count(signal => signal.Kind == PreviewThumbnailSignalKind.Failed) == 1
                   && service.GetMetrics().FailureCacheHitCount == 1,
                "Stable preview failure caching repeated decode or issue signals.");

            var alternateBucket = new PreviewThumbnailRequest(
                request.ProjectKey,
                request.CanonicalPath,
                request.ScanFileLength,
                request.ScanLastWriteTimeUtc,
                request.PreviewFormat,
                321,
                request.Generation);
            using (var secondBucket = service.Acquire(alternateBucket))
            {
                assert((await secondBucket.Completion).Status == PreviewThumbnailStatus.Corrupt,
                    "A stable preview failure did not remain isolated by thumbnail size bucket.");
            }

            assert(decoder.CallCount == 2
                   && signals.Count(signal => signal.Kind == PreviewThumbnailSignalKind.Failed) == 1,
                "One preview version published duplicate failure signals across size buckets.");

            using (var retry = service.Acquire(alternateBucket, retryFailed: true))
            {
                assert((await retry.Completion).IsSuccess,
                    "An explicit retry did not replace the cached preview failure.");
            }

            await WaitUntilAsync(() => signals.Any(signal =>
                signal.Kind == PreviewThumbnailSignalKind.Resolved));
            assert(decoder.CallCount == 3
                   && signals.Count(signal => signal.Kind == PreviewThumbnailSignalKind.Resolved) == 1,
                "A successful preview retry did not emit one resolve signal.");
        }

        var releaseStale = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleDecoder = new ControlledDecoder(async (_, _) =>
        {
            await releaseStale.Task;
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(5, 5));
        });
        using (var service = new PreviewThumbnailService(staleDecoder))
        {
            service.SetGeneration(6);
            using var oldLease = service.Acquire(CreateFakeRequest(1, generation: 6));
            await WaitUntilAsync(() => staleDecoder.CallCount == 1);
            service.SetGeneration(7);
            var stale = await oldLease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            releaseStale.TrySetResult(true);
            await WaitUntilAsync(() => service.GetMetrics().ActiveDecodes == 0);
            var metrics = service.GetMetrics();
            assert(stale.Status == PreviewThumbnailStatus.Stale
                   && metrics.CacheEntryCount == 0
                   && metrics.PendingDecodes == 0
                   && metrics.StaleDiscardCount >= 1,
                "A stale generation completion reached the cache or remained pending.");

            using var mismatched = service.Acquire(CreateFakeRequest(2, generation: 6));
            assert((await mismatched.Completion).Status == PreviewThumbnailStatus.Stale,
                "A request from an old snapshot generation entered the decoder queue.");
        }
    }

    private static async Task VerifyViewportControlAsync(Action<bool, string> assert)
    {
        await RunOnStaAsync(async () =>
        {
            var decoder = new ControlledDecoder((_, _) => Task.FromResult(
                PreviewThumbnailResult.Ready(CreateFrozenBitmap(16, 10))));
            using var service = new PreviewThumbnailService(decoder);
            service.SetGeneration(8);

            var scrollViewer = new ScrollViewer
            {
                Width = 240,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = new StackPanel()
            };
            var stack = (StackPanel)scrollViewer.Content;
            stack.Children.Add(new Border { Height = 1_000 });
            var image = new ThumbnailPreviewImage
            {
                Width = 160,
                Height = 100,
                ThumbnailService = service,
                ProjectKey = "viewport-project",
                SourcePath = Path.Combine(Path.GetTempPath(), "viewport-preview.png"),
                ScanFileLength = 100,
                ScanLastWriteTimeUtc = DateTimeOffset.UnixEpoch,
                PreviewFormat = ".png",
                SnapshotGeneration = 8,
                DecodePixelWidth = 193
            };
            stack.Children.Add(image);
            stack.Children.Add(new Border { Height = 1_000 });

            var window = new Window
            {
                Width = 260,
                Height = 220,
                Left = -10_000,
                Top = -10_000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = scrollViewer
            };
            try
            {
                window.Show();
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await Task.Delay(60);
                assert(decoder.CallCount == 0 && service.GetMetrics().ObserverCount == 0,
                    "ThumbnailPreviewImage acquired a lease outside viewport plus one-row overscan.");

                scrollViewer.ScrollToVerticalOffset(701);
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => decoder.CallCount == 1 && image.Source is not null,
                    dispatcher: window.Dispatcher);
                assert(image.ThumbnailStatus == PreviewThumbnailStatus.Ready,
                    "A thumbnail entering viewport overscan did not display its frozen result.");

                scrollViewer.ScrollToVerticalOffset(0);
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => service.GetMetrics().ObserverCount == 0 && image.Source is null,
                    dispatcher: window.Dispatcher);
                assert(decoder.CallCount == 1,
                    "Leaving viewport overscan started an unexpected thumbnail decode.");

                scrollViewer.ScrollToVerticalOffset(701);
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => service.GetMetrics().ObserverCount == 1 && image.Source is not null,
                    dispatcher: window.Dispatcher);
                assert(decoder.CallCount == 1,
                    "Re-entering viewport overscan missed the released thumbnail cache entry.");

                image.SnapshotGeneration = 7;
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => service.GetMetrics().ObserverCount == 0
                          && image.ThumbnailStatus == PreviewThumbnailStatus.Stale,
                    dispatcher: window.Dispatcher);
                image.SnapshotGeneration = 8;
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => service.GetMetrics().ObserverCount == 1 && image.Source is not null,
                    dispatcher: window.Dispatcher);
                assert(decoder.CallCount == 1,
                    "Generation recycling retained or re-decoded the old visible lease.");

                image.SourcePath = Path.Combine(Path.GetTempPath(), "viewport-preview-2.png");
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                await WaitUntilAsync(
                    () => decoder.CallCount == 2 && image.Source is not null,
                    dispatcher: window.Dispatcher);
                assert(service.GetMetrics().ObserverCount == 1,
                    "Recycling/source change retained the old thumbnail lease.");

                window.Content = null;
                await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
                assert(service.GetMetrics().ObserverCount == 0,
                    "Unloading ThumbnailPreviewImage did not release its lease.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task VerifyBrowseIntegrationSurfaceAsync(
        Action<bool, string> assert)
    {
        var browseType = typeof(WallpaperField.ViewModels.BrowsePageViewModel);
        assert(
            browseType.GetProperty("ThumbnailService")?.PropertyType
                == typeof(PreviewThumbnailService),
            "BrowsePageViewModel does not expose its application-scoped thumbnail service.");
        assert(
            browseType.GetProperty("ThumbnailGeneration")?.PropertyType == typeof(long),
            "BrowsePageViewModel does not expose the current thumbnail generation.");
        assert(
            browseType.GetEvent("PreviewStatusChanged")?.EventHandlerType
                == typeof(EventHandler<PreviewThumbnailSignalEventArgs>),
            "BrowsePageViewModel lacks the preview failure/resolve signal seam for Task 6.");

        var shell = WallpaperField.Composition.AppComposition.CreateShellViewModel();
        try
        {
            var browse = shell.BrowsePageViewModel;
            var composedBrowse = WallpaperField.Composition.AppComposition
                .CreateBrowsePageViewModel(shell);
            var decoderField = typeof(PreviewThumbnailService).GetField(
                "_decoder",
                BindingFlags.Instance | BindingFlags.NonPublic);
            assert(ReferenceEquals(browse, composedBrowse)
                   && decoderField?.GetValue(browse.ThumbnailService)
                       is WpfPreviewThumbnailDecoder,
                "AppComposition does not own one shared real WPF thumbnail service for Browse.");

            var signals = new List<PreviewThumbnailSignalEventArgs>();
            browse.PreviewStatusChanged += (_, args) => signals.Add(args);
            var missingRequest = new PreviewThumbnailRequest(
                "composed-preview-project",
                Path.Combine(
                    Path.GetTempPath(),
                    $"WallpaperField-Missing-{Guid.NewGuid():N}.png"),
                1,
                DateTimeOffset.UnixEpoch,
                ".png",
                193,
                browse.ThumbnailGeneration);
            using (var first = browse.ThumbnailService.Acquire(missingRequest))
            {
                assert((await first.Completion).Status == PreviewThumbnailStatus.Missing,
                    "The composed Browse thumbnail service did not execute its real decoder.");
            }

            using (var cached = browse.ThumbnailService.Acquire(missingRequest))
            {
                _ = await cached.Completion;
            }

            await WaitUntilAsync(() => signals.Count > 0);
            assert(signals.Count(signal =>
                       signal.Kind == PreviewThumbnailSignalKind.Failed
                       && signal.ProjectKey == missingRequest.ProjectKey
                       && signal.PreviewVersion == missingRequest.PreviewVersion) == 1,
                "Browse did not publish one deduplicated ProjectKey/preview-version failure signal.");
        }
        finally
        {
            shell.Dispose();
        }
    }

    private static PreviewThumbnailDecodeRequest CreateDecodeRequest(
        string path,
        int requestedPixelWidth)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new PreviewThumbnailDecodeRequest(
            Path.GetFullPath(path),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc),
            Path.GetExtension(path).ToLowerInvariant(),
            PreviewThumbnailLimits.GetSizeBucket(requestedPixelWidth));
    }

    private static PreviewThumbnailRequest CreateFakeRequest(
        int index,
        long generation)
        => new(
            $"project-{index:D4}",
            Path.Combine(Path.GetTempPath(), "WallpaperField-Preview-Fake", $"{index:D4}.png"),
            100 + index,
            DateTimeOffset.UnixEpoch.AddSeconds(index),
            ".png",
            193,
            generation);

    private static BitmapSource CreateFrozenBitmap(
        int width,
        int height,
        byte blue = 0x20,
        byte green = 0x80,
        byte red = 0xF0)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue;
            pixels[index + 1] = green;
            pixels[index + 2] = red;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte Blue, byte Green, byte Red, byte Alpha) ReadFirstBgraPixel(
        BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(
            bitmap,
            PixelFormats.Bgra32,
            null,
            0);
        converted.Freeze();
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null,
        Dispatcher? dispatcher = null)
    {
        var deadline = Stopwatch.GetTimestamp()
                       + (long)((timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds
                                * Stopwatch.Frequency);
        while (!predicate())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Timed out waiting for deterministic preview test state.");
            }

            if (dispatcher is null)
            {
                await Task.Delay(10);
            }
            else
            {
                await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                await Task.Delay(10);
            }
        }
    }

    private static Task DrainAsync(Dispatcher dispatcher, DispatcherPriority priority)
        => dispatcher.InvokeAsync(static () => { }, priority).Task;

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WallpaperField.ProjectBrowserPreviewRegressionTests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return AwaitThreadAsync(completion.Task, thread);
    }

    private static async Task AwaitThreadAsync(Task completion, Thread thread)
    {
        await completion.WaitAsync(TimeSpan.FromSeconds(20));
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The preview WPF regression dispatcher did not stop.");
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
            ?? throw new InvalidOperationException("Unable to start mklink for preview fixture.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException(
                $"Could not create preview junction ({process.ExitCode}): "
                + $"{standardOutput} {standardError}");
        }
    }

    private static void VerifyConstant<T>(
        Type declaringType,
        string fieldName,
        T expected,
        Action<bool, string> assert)
        where T : IEquatable<T>
    {
        var field = declaringType.GetField(
            fieldName,
            BindingFlags.Public | BindingFlags.Static);
        var actual = field?.GetRawConstantValue();
        assert(
            actual is T typed && typed.Equals(expected),
            $"{declaringType.FullName}.{fieldName} must equal {expected}.");
    }

    private sealed class ControlledDecoder : IPreviewThumbnailDecoder
    {
        private readonly Func<PreviewThumbnailDecodeRequest, CancellationToken,
            Task<PreviewThumbnailResult>> _handler;
        private int _activeCount;
        private int _callCount;
        private int _peakActiveCount;

        internal ControlledDecoder(
            Func<PreviewThumbnailDecodeRequest, CancellationToken,
                Task<PreviewThumbnailResult>> handler)
        {
            _handler = handler;
        }

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int PeakActiveCount => Volatile.Read(ref _peakActiveCount);

        public async Task<PreviewThumbnailResult> DecodeAsync(
            PreviewThumbnailDecodeRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCount);
            UpdateMaximum(ref _peakActiveCount, active);
            try
            {
                return await _handler(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
            }
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref target, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class PreviewFixture : IDisposable
    {
        internal PreviewFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"WallpaperField-Preview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string CreateDirectory(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string WriteBytes(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal string CreateSparseFile(string relativePath, long length)
        {
            var path = Path.Combine(Root, relativePath);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.SetLength(length);
            return path;
        }

        internal string WriteImage(
            string relativePath,
            BitmapEncoder encoder,
            int width,
            int height,
            int frameCount = 1)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bitmap = CreateFrozenBitmap(width, height);
            for (var frame = 0; frame < frameCount; frame++)
            {
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
            }

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            return path;
        }

        internal string WriteGif(
            string relativePath,
            BitmapSource firstFrame,
            BitmapSource secondFrame)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(firstFrame));
            encoder.Frames.Add(BitmapFrame.Create(secondFrame));
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
