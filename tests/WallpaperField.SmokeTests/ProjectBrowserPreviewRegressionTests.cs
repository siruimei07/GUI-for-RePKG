using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Application;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class ProjectBrowserPreviewRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        VerifySurfaceAndLimits(assert);
        await VerifyRealDecoderAsync(assert);
        await VerifyImmutableValidatedSourceAsync(assert);
        await VerifyHandleIdentityAgainstAncestorSwapAsync(assert);
        await VerifyInFlightAndConcurrencyAsync(assert);
        await VerifyHealthyCrossBucketConcurrencyAsync(assert);
        await VerifySharedFailureRecoveryAsync(assert);
        await VerifyReadyReplayRecoveryEpochAsync(assert);
        await VerifyCacheAndLeaseLifecycleAsync(assert);
        await VerifyMaliciousResultNormalizationAsync(assert);
        await VerifyFatalDecoderCompletionAsync(assert);
        await VerifyCancellationDisposeReentrancyAsync(assert);
        await VerifyOrderedSignalLifecycleAsync(assert);
        await VerifyFailureRetryAndGenerationAsync(assert);
        await VerifyViewportControlAsync(assert);
        await VerifyBrowseDisposeCallbackBarrierAsync(assert);
        await VerifyBrowseSignalAffinityAsync(assert);
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
        var highBitSource = CreateFrozenRgba64Bitmap(17, 3);
        var highBitPath = fixture.WriteBitmap(
            "preview-rgba64.png",
            new PngBitmapEncoder(),
            highBitSource);
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
        assert(results.All(result => result.Bitmap is WriteableBitmap),
            "A decoded thumbnail retained a BitmapImage/FormatConvertedBitmap source graph instead of detached pixels.");
        assert(results.All(result => result.Bitmap!.PixelWidth <= 256),
            "A decoded project thumbnail ignored its target size bucket.");
        var gifPixel = ReadFirstBgraPixel(results[2].Bitmap!);
        assert(gifPixel.Red > gifPixel.Blue,
            "The GIF thumbnail did not retain the static first frame.");
        var highBitResult = await decoder.DecodeAsync(
            CreateDecodeRequest(highBitPath, 64),
            CancellationToken.None);
        assert(highBitResult.IsSuccess
               && highBitResult.Bitmap is WriteableBitmap
               && highBitResult.DecodedBytes == CalculateAlignedBitmapBytes(highBitResult.Bitmap!)
               && highBitResult.DecodedBytes <= PreviewThumbnailLimits.MaximumDecodedCacheBytes,
            "A real high-bit-depth PNG reported inaccurate decoded/cache bytes.");
        assert(PreviewThumbnailResult.Ready(highBitSource).DecodedBytes
               == CalculateAlignedBitmapBytes(highBitSource),
            "PreviewThumbnailResult.Ready hard-coded 32bpp byte accounting.");
        using (var highBitService = new PreviewThumbnailService(
                   new WpfPreviewThumbnailDecoder()))
        {
            highBitService.SetGeneration(1);
            var highBitInfo = new FileInfo(highBitPath);
            highBitInfo.Refresh();
            var highBitRequest = new PreviewThumbnailRequest(
                "real-high-bit-cache",
                highBitPath,
                highBitInfo.Length,
                new DateTimeOffset(highBitInfo.LastWriteTimeUtc),
                ".png",
                64,
                1);
            PreviewThumbnailResult cachedHighBit;
            using (var lease = highBitService.Acquire(highBitRequest))
            {
                cachedHighBit = await lease.Completion;
            }

            var highBitMetrics = highBitService.GetMetrics();
            assert(cachedHighBit.Bitmap?.Format == PixelFormats.Bgra32
                   && highBitMetrics.CacheEntryCount == 1
                   && highBitMetrics.CacheDecodedBytes == cachedHighBit.DecodedBytes
                   && highBitMetrics.CacheDecodedBytes
                   <= PreviewThumbnailLimits.MaximumDecodedCacheBytes,
                "A real high-bit-depth PNG did not normalize to exact bounded cache bytes.");
        }
        var ownershipConstructor = typeof(WpfPreviewThumbnailDecoder).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(Action<int>),
                typeof(Action),
                typeof(Action<WeakReference<byte[]>, WeakReference<BitmapSource>>)
            ],
            modifiers: null);
        WeakReference<byte[]>? encodedOwner = null;
        WeakReference<BitmapSource>? sourceOwner = null;
        assert(ownershipConstructor is not null,
            "The real decoder lacks a deterministic detached-source ownership probe.");
        if (ownershipConstructor is not null)
        {
            var ownershipDecoder = (WpfPreviewThumbnailDecoder)ownershipConstructor.Invoke(
            [
                (Action<int>)(_ => { }),
                null!,
                (Action<WeakReference<byte[]>, WeakReference<BitmapSource>>)(
                    (encoded, source) =>
                    {
                        encodedOwner = encoded;
                        sourceOwner = source;
                    })
            ]);
            var detachedResult = await ownershipDecoder.DecodeAsync(
                CreateDecodeRequest(highBitPath, 64),
                CancellationToken.None);
            var oldOwnersCollected = await WaitForWeakOwnersCollectionAsync(
                encodedOwner,
                sourceOwner);
            assert(detachedResult.Bitmap is WriteableBitmap
                   && oldOwnersCollected,
                "A detached thumbnail retained its encoded buffer or WPF source surface after completion.");
        }
        assert(workerThreadIds.Count == 4
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

    private static async Task VerifyImmutableValidatedSourceAsync(
        Action<bool, string> assert)
    {
        using var fixture = new PreviewFixture();
        var sourcePath = fixture.WriteBitmap(
            "stable-source.png",
            new PngBitmapEncoder(),
            CreateFrozenBitmap(16, 10, blue: 0, green: 0, red: 255));
        var replacementPath = fixture.WriteBitmap(
            "replacement.png",
            new PngBitmapEncoder(),
            CreateFrozenBitmap(16, 10, blue: 255, green: 0, red: 0));
        var request = CreateDecodeRequest(sourcePath, 64);
        var envelopeValidated = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPixels = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var constructor = typeof(WpfPreviewThumbnailDecoder).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Action<int>), typeof(Action)],
            modifiers: null);
        assert(constructor is not null,
            "The real decoder lacks a deterministic metadata-to-pixel stable-source barrier.");
        if (constructor is null)
        {
            return;
        }

        var decoder = (WpfPreviewThumbnailDecoder)constructor.Invoke(
        [
            (Action<int>)(_ => { }),
            (Action)(() =>
            {
                envelopeValidated.TrySetResult(true);
                allowPixels.Task.GetAwaiter().GetResult();
            })
        ]);
        var completion = decoder.DecodeAsync(request, CancellationToken.None);
        await envelopeValidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        File.Move(replacementPath, sourcePath, overwrite: true);
        allowPixels.TrySetResult(true);
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        var pixel = ReadFirstBgraPixel(result.Bitmap!);
        assert(result.IsSuccess && pixel.Red > pixel.Blue,
            "A pathname swap after metadata validation changed the decoded pixel source.");
    }

    private static async Task VerifyHealthyCrossBucketConcurrencyAsync(
        Action<bool, string> assert)
    {
        var release256 = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release384 = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = new ControlledDecoder(async (request, _) =>
        {
            if (request.SizeBucket == 256)
            {
                await release256.Task;
            }
            else
            {
                await release384.Task;
            }

            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(8, 8));
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(12);
        var request256 = CreateFakeRequest(12, generation: 12);
        var request384 = new PreviewThumbnailRequest(
            request256.ProjectKey,
            request256.CanonicalPath,
            request256.ScanFileLength,
            request256.ScanLastWriteTimeUtc,
            request256.PreviewFormat,
            321,
            request256.Generation);
        using var lease256 = service.Acquire(request256);
        using var lease384 = service.Acquire(request384);
        await WaitUntilAsync(() => decoder.CallCount == 2);

        release384.TrySetResult(true);
        var result384 = await lease384.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        release256.TrySetResult(true);
        var result256 = await lease256.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        assert(result256.IsSuccess
               && result384.IsSuccess
               && service.GetMetrics().StaleDiscardCount == 0,
            "A healthy faster size bucket incorrectly retired a slower healthy decode as Stale.");
    }

    private static async Task VerifySharedFailureRecoveryAsync(
        Action<bool, string> assert)
    {
        var completedStateRecovered = await RunSharedFailureRecoveryScenarioAsync(
            releaseFailureLeasesBeforeRetry: false);
        var releasedCacheRecovered = await RunSharedFailureRecoveryScenarioAsync(
            releaseFailureLeasesBeforeRetry: true);

        assert(completedStateRecovered,
            "A completed shared failure retry did not resolve every ProjectKey identity exactly once.");
        assert(releasedCacheRecovered,
            "A cached shared failure retry did not resolve every ProjectKey identity exactly once.");
    }

    private static async Task<bool> RunSharedFailureRecoveryScenarioAsync(
        bool releaseFailureLeasesBeforeRetry)
    {
        var releaseInitialFailure = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var decoder = new ControlledDecoder(async (_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                await releaseInitialFailure.Task;
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_SHARED_FAILURE",
                    "共享预览图损坏。");
            }

            return attempt == 2
                ? PreviewThumbnailResult.Ready(CreateFrozenBitmap(8, 6))
                : PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_FUTURE_FAILURE",
                    "恢复后的新解码失败。");
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(92);
        var signals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        service.StatusChanged += (_, args) => signals.Enqueue(args);
        var requestA = CreateFakeRequest(92, generation: 92);
        var requestB = WithProjectKey(requestA, "project-shared-b");
        var leaseA = service.Acquire(requestA);
        var leaseB = service.Acquire(requestB);
        await WaitUntilAsync(() => decoder.CallCount == 1);
        releaseInitialFailure.TrySetResult(true);
        var initialResults = await Task.WhenAll(
            leaseA.Completion,
            leaseB.Completion).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => signals.Count(signal =>
            signal.Kind == PreviewThumbnailSignalKind.Failed) == 2);

        if (releaseFailureLeasesBeforeRetry)
        {
            leaseA.Dispose();
            leaseB.Dispose();
        }

        using (var retry = service.Acquire(requestA, retryFailed: true))
        {
            _ = await retry.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await WaitUntilAsync(() => signals.Any(signal =>
            signal.Kind == PreviewThumbnailSignalKind.Resolved));
        await Task.Delay(50);
        var immediatelyAfterRetry = signals.ToArray();
        var bothResolvedImmediately = new[] { requestA.ProjectKey, requestB.ProjectKey }
            .All(projectKey => immediatelyAfterRetry.Count(signal =>
                signal.Kind == PreviewThumbnailSignalKind.Resolved
                && signal.ProjectKey == projectKey) == 1);

        using (var replayB = service.Acquire(requestB))
        {
            _ = await replayB.Completion;
        }

        await Task.Delay(50);
        var afterReplay = signals.ToArray();
        var replayDidNotFinishRecovery = afterReplay.Length == immediatelyAfterRetry.Length;

        var futureRequestB = new PreviewThumbnailRequest(
            requestB.ProjectKey,
            requestB.CanonicalPath,
            requestB.ScanFileLength,
            requestB.ScanLastWriteTimeUtc,
            requestB.PreviewFormat,
            500,
            requestB.Generation);
        PreviewThumbnailResult futureFailure;
        using (var future = service.Acquire(futureRequestB))
        {
            futureFailure = await future.Completion;
        }

        await WaitUntilAsync(() => signals.Count(signal =>
            signal.Kind == PreviewThumbnailSignalKind.Failed
            && signal.ProjectKey == requestB.ProjectKey) == 2);
        var beforeOldReadyReplay = signals.Count;
        using (var oldReadyReplay = service.Acquire(requestA))
        {
            _ = await oldReadyReplay.Completion;
        }

        await Task.Delay(50);
        var finalSignals = signals.ToArray();
        if (!releaseFailureLeasesBeforeRetry)
        {
            leaseA.Dispose();
            leaseB.Dispose();
        }

        return initialResults.All(result => result.IsStableFailure)
               && bothResolvedImmediately
               && replayDidNotFinishRecovery
               && futureFailure.IsStableFailure
               && finalSignals.Length == beforeOldReadyReplay
               && finalSignals.Count(signal =>
                   signal.Kind == PreviewThumbnailSignalKind.Failed
                   && signal.ProjectKey == requestA.ProjectKey) == 1
               && finalSignals.Count(signal =>
                   signal.Kind == PreviewThumbnailSignalKind.Failed
                   && signal.ProjectKey == requestB.ProjectKey) == 2
               && finalSignals.Count(signal =>
                   signal.Kind == PreviewThumbnailSignalKind.Resolved) == 2;
    }

    private static async Task VerifyReadyReplayRecoveryEpochAsync(
        Action<bool, string> assert)
    {
        var completedReplayPreservedFailure = await RunReadyReplayScenarioAsync(
            releaseReadyLeaseToCache: false);
        var cacheReplayPreservedFailure = await RunReadyReplayScenarioAsync(
            releaseReadyLeaseToCache: true);

        assert(completedReplayPreservedFailure && cacheReplayPreservedFailure,
            "A completed/cache Ready replay incorrectly advanced recovery and suppressed a later real failure.");
    }

    private static async Task<bool> RunReadyReplayScenarioAsync(
        bool releaseReadyLeaseToCache)
    {
        var failureStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = new ControlledDecoder(async (request, _) =>
        {
            if (request.SizeBucket == 256)
            {
                return PreviewThumbnailResult.Ready(CreateFrozenBitmap(8, 6));
            }

            failureStarted.TrySetResult(true);
            await releaseFailure.Task;
            return PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_REAL_LATE_FAILURE",
                "后续尺寸解码失败。");
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(93);
        var signals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        service.StatusChanged += (_, args) => signals.Enqueue(args);
        var requestA256 = CreateFakeRequest(93, generation: 93);
        var requestB256 = WithProjectKey(requestA256, "project-replay-b");
        var requestA384 = WithRequestedWidth(requestA256, 321);
        var requestB384 = WithProjectKey(requestA384, requestB256.ProjectKey);
        var readyLease = service.Acquire(requestA256);
        _ = await readyLease.Completion;
        var failureLeaseA = service.Acquire(requestA384);
        var failureLeaseB = service.Acquire(requestB384);
        await failureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        if (releaseReadyLeaseToCache)
        {
            readyLease.Dispose();
        }

        using (var replay = service.Acquire(
                   releaseReadyLeaseToCache ? requestA256 : requestB256))
        {
            _ = await replay.Completion;
        }

        releaseFailure.TrySetResult(true);
        var results = await Task.WhenAll(
            failureLeaseA.Completion,
            failureLeaseB.Completion).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        failureLeaseA.Dispose();
        failureLeaseB.Dispose();
        if (!releaseReadyLeaseToCache)
        {
            readyLease.Dispose();
        }

        var published = signals.ToArray();
        return results.All(result => result.Status == PreviewThumbnailStatus.Corrupt)
               && published.Count(signal =>
                   signal.Kind == PreviewThumbnailSignalKind.Failed) == 2
               && published.Select(signal => signal.ProjectKey).ToHashSet()
                   .SetEquals([requestA256.ProjectKey, requestB256.ProjectKey]);
    }

    private static async Task VerifyHandleIdentityAgainstAncestorSwapAsync(
        Action<bool, string> assert)
    {
        using var fixture = new PreviewFixture();
        var safeDirectory = fixture.CreateDirectory("safe-parent");
        var attackerDirectory = fixture.CreateDirectory("attacker-parent");
        var safePath = fixture.WriteBitmap(
            Path.Combine("safe-parent", "preview.png"),
            new PngBitmapEncoder(),
            CreateFrozenBitmap(16, 10, blue: 0, green: 0, red: 255));
        var attackerPath = fixture.WriteBitmap(
            Path.Combine("attacker-parent", "preview.png"),
            new PngBitmapEncoder(),
            CreateFrozenBitmap(16, 10, blue: 255, green: 0, red: 0));
        EqualizeFileEnvelope(safePath, attackerPath);
        var request = CreateDecodeRequest(safePath, 64);
        var holdingDirectory = Path.Combine(fixture.Root, "safe-parent-holding");
        var constructor = typeof(WpfPreviewThumbnailDecoder).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Action<int>), typeof(Action), typeof(Action), typeof(Action)],
            modifiers: null);
        assert(constructor is not null,
            "The real decoder lacks deterministic before-open/after-handle-open race barriers.");
        if (constructor is null)
        {
            return;
        }

        var swapped = false;
        var restored = false;
        var decoder = (WpfPreviewThumbnailDecoder)constructor.Invoke(
        [
            (Action<int>)(_ => { }),
            null!,
            (Action)(() =>
            {
                Directory.Move(safeDirectory, holdingDirectory);
                CreateDirectoryJunction(safeDirectory, attackerDirectory);
                swapped = true;
            }),
            (Action)(() =>
            {
                Directory.Delete(safeDirectory);
                Directory.Move(holdingDirectory, safeDirectory);
                restored = true;
            })
        ]);

        PreviewThumbnailResult result;
        try
        {
            result = await decoder.DecodeAsync(request, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(safeDirectory)
                && (File.GetAttributes(safeDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(safeDirectory);
            }

            if (Directory.Exists(holdingDirectory) && !Directory.Exists(safeDirectory))
            {
                Directory.Move(holdingDirectory, safeDirectory);
            }
        }

        assert(swapped
               && restored
               && result.Status == PreviewThumbnailStatus.ReparsePoint
               && result.Bitmap is null,
            "An ancestor junction swap made the decoder consume a different final handle path.");
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

        const long fakeDecodedBytes = 4L * 1024 * 1024;
        var byteBudgetBitmap = CreateFrozenBitmap(1024, 1024);
        var byteDecoder = new ControlledDecoder((_, _) => Task.FromResult(
            PreviewThumbnailResult.Ready(byteBudgetBitmap)));
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

            PreviewThumbnailResult recoveredOriginal;
            using (var recovered = service.Acquire(request))
            {
                recoveredOriginal = await recovered.Completion;
            }

            assert(recoveredOriginal.IsSuccess
                   && decoder.CallCount == 4
                   && signals.Count(signal => signal.Kind == PreviewThumbnailSignalKind.Failed) == 1,
                "A successful cross-bucket retry left an older failure cache able to reopen Failed.");
        }

        var oldStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bucket384Attempts = 0;
        var bucket256Attempts = 0;
        var concurrentDecoder = new ControlledDecoder(async (decodeRequest, _) =>
        {
            if (decodeRequest.SizeBucket == 256
                && Interlocked.Increment(ref bucket256Attempts) == 1)
            {
                oldStarted.TrySetResult(true);
                await releaseOld.Task;
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_OLD_FAILURE",
                    "旧尺寸解码失败。");
            }

            if (decodeRequest.SizeBucket == 384
                && Interlocked.Increment(ref bucket384Attempts) == 1)
            {
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_BUCKET_FAILURE",
                    "预览图损坏。");
            }

            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(6, 4));
        });
        using (var service = new PreviewThumbnailService(concurrentDecoder))
        {
            service.SetGeneration(52);
            var signals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
            service.StatusChanged += (_, args) => signals.Enqueue(args);
            var request256 = CreateFakeRequest(52, generation: 52);
            var request384 = new PreviewThumbnailRequest(
                request256.ProjectKey,
                request256.CanonicalPath,
                request256.ScanFileLength,
                request256.ScanLastWriteTimeUtc,
                request256.PreviewFormat,
                321,
                request256.Generation);
            var request256B = WithProjectKey(request256, "project-concurrent-b");
            var request384B = WithProjectKey(request384, request256B.ProjectKey);
            var oldLeaseA = service.Acquire(request256);
            var oldLeaseB = service.Acquire(request256B);
            var oldRemainedPendingUntilDecode = false;
            PreviewThumbnailResult[] oldResults;
            try
            {
                await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using (var failure384A = service.Acquire(request384))
                using (var failure384B = service.Acquire(request384B))
                {
                    _ = await Task.WhenAll(
                        failure384A.Completion,
                        failure384B.Completion);
                }

                await WaitUntilAsync(() => signals.Count(signal =>
                    signal.Kind == PreviewThumbnailSignalKind.Failed) == 2);
                using (var retry384 = service.Acquire(request384, retryFailed: true))
                {
                    assert((await retry384.Completion).IsSuccess,
                        "The concurrent cross-bucket retry fixture did not recover.");
                }
                await WaitUntilAsync(() => signals.Count(signal =>
                    signal.Kind == PreviewThumbnailSignalKind.Resolved) == 2);

                var oldCompletion = Task.WhenAll(
                    oldLeaseA.Completion,
                    oldLeaseB.Completion);
                oldRemainedPendingUntilDecode = await Task.WhenAny(
                                                    oldCompletion,
                                                    Task.Delay(TimeSpan.FromSeconds(1)))
                                                != oldCompletion;
                releaseOld.TrySetResult(true);
                oldResults = await oldCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                releaseOld.TrySetResult(true);
                oldLeaseA.Dispose();
                oldLeaseB.Dispose();
            }

            using var recovered256 = service.Acquire(request256);
            var recoveredResult = await recovered256.Completion;
            await WaitUntilAsync(() => service.GetMetrics().ActiveDecodes == 0);
            var published = signals.ToArray();
            assert(oldRemainedPendingUntilDecode
                   && oldResults.All(result =>
                       result.Status == PreviewThumbnailStatus.Stale)
                   && recoveredResult.IsSuccess
                   && published.Select(signal => signal.Kind).SequenceEqual(
                   [
                       PreviewThumbnailSignalKind.Failed,
                       PreviewThumbnailSignalKind.Failed,
                       PreviewThumbnailSignalKind.Resolved,
                       PreviewThumbnailSignalKind.Resolved
                   ]),
                "A concurrent old-bucket completion survived recovery and reopened Failed.");
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

    private static async Task VerifyMaliciousResultNormalizationAsync(
        Action<bool, string> assert)
    {
        var bitmap = CreateFrozenBitmap(1, 1);
        var poisonedFailure = new PreviewThumbnailResult(
            PreviewThumbnailStatus.Corrupt,
            bitmap,
            long.MaxValue,
            "PREVIEW_CORRUPT",
            "预览图损坏。");
        var failureDecoder = new ControlledDecoder((_, _) => Task.FromResult(poisonedFailure));
        using (var service = new PreviewThumbnailService(failureDecoder))
        {
            service.SetGeneration(41);
            var request = CreateFakeRequest(41, generation: 41);
            using (var first = service.Acquire(request))
            {
                var result = await first.Completion;
                assert(result.Status == PreviewThumbnailStatus.Corrupt
                       && result.Bitmap is null
                       && result.DecodedBytes == 0,
                    "A stable failure retained an attacker-controlled bitmap or byte count.");
            }

            var metrics = service.GetMetrics();
            assert(metrics.CacheDecodedBytes == 0,
                "A poisoned stable-failure byte count overflowed cache accounting.");
        }

        var poisonedReady = new PreviewThumbnailResult(
            PreviewThumbnailStatus.Ready,
            bitmap,
            long.MaxValue,
            null,
            null);
        var readyDecoder = new ControlledDecoder((_, _) => Task.FromResult(poisonedReady));
        using (var service = new PreviewThumbnailService(readyDecoder))
        {
            service.SetGeneration(42);
            using var lease = service.Acquire(CreateFakeRequest(42, generation: 42));
            var result = await lease.Completion;
            assert(result.IsSuccess
                   && result.DecodedBytes == CalculateAlignedBitmapBytes(bitmap),
                "Ready-result normalization trusted a forged decoded-byte count.");
        }

        var unfrozenBitmap = new WriteableBitmap(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        var unfrozenDecoder = new ControlledDecoder((_, _) => Task.FromResult(
            new PreviewThumbnailResult(
                PreviewThumbnailStatus.Ready,
                unfrozenBitmap,
                4,
                null,
                null)));
        using (var service = new PreviewThumbnailService(unfrozenDecoder))
        {
            service.SetGeneration(44);
            using var lease = service.Acquire(CreateFakeRequest(45, generation: 44));
            var result = await lease.Completion;
            assert(result.Status == PreviewThumbnailStatus.Corrupt
                   && result.FailureCode == "PREVIEW_INVALID_RESULT"
                   && result.Bitmap is null
                   && result.DecodedBytes == 0,
                "Ready-result normalization accepted an unfrozen attacker-controlled bitmap.");
        }
    }

    private static async Task VerifyFatalDecoderCompletionAsync(
        Action<bool, string> assert)
    {
        var attempts = 0;
        var decoder = new ControlledDecoder((_, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            return attempt == 1
                ? Task.FromException<PreviewThumbnailResult>(
                    new OutOfMemoryException("Injected fatal preview decoder fault."))
                : Task.FromResult(
                    PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4)));
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(43);

        var fatalLease = service.Acquire(CreateFakeRequest(43, generation: 43));
        try
        {
            var terminal = await Task.WhenAny(
                               fatalLease.Completion,
                               Task.Delay(TimeSpan.FromSeconds(1)))
                           == fatalLease.Completion;
            assert(terminal,
                "A fatal thumbnail decoder fault left its lease completion permanently pending.");

            var preservedFatal = false;
            if (terminal)
            {
                try
                {
                    _ = await fatalLease.Completion;
                }
                catch (OutOfMemoryException)
                {
                    preservedFatal = true;
                }
            }

            assert(preservedFatal,
                "A fatal thumbnail decoder fault was swallowed or converted into a stable failure.");
        }
        finally
        {
            fatalLease.Dispose();
        }

        using (var recovery = service.Acquire(CreateFakeRequest(44, generation: 43)))
        {
            assert((await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess,
                "A fatal thumbnail decoder fault leaked the concurrency gate.");
        }

        await WaitUntilAsync(() => service.GetMetrics().PendingDecodes == 0);
        var metrics = service.GetMetrics();
        assert(metrics.ActiveDecodes == 0
               && metrics.PendingDecodes == 0
               && metrics.ObserverCount == 0,
            "A fatal thumbnail decoder fault left active, pending, or observed operation state behind.");
    }

    private static async Task VerifyCancellationDisposeReentrancyAsync(
        Action<bool, string> assert)
    {
        var serviceResult = await RunServiceCancellationDisposeScenarioAsync();
        var browseResult = await RunBrowseCancellationDisposeScenarioAsync();
        var combinedServiceResult = await RunCombinedServiceDisposeScenarioAsync();
        var combinedBrowseResult = await RunCombinedBrowseDisposeScenarioAsync();

        assert(serviceResult.ReentrantReturned,
            "A cancellation callback re-entering Service Dispose deadlocked its owning Dispose.");
        assert(browseResult.ReentrantReturned,
            "A cancellation callback re-entering Browse Dispose deadlocked its owning Dispose.");
        assert(serviceResult.ConcurrentWaited && browseResult.ConcurrentWaited,
            "An unrelated concurrent Dispose returned before cancellation callback cleanup completed.");
        assert(combinedServiceResult.LaterHandlerSuppressed,
            "A same-owner Service Dispose returned before closing captured StatusChanged handlers.");
        assert(combinedBrowseResult.LaterHandlerSuppressed,
            "A same-owner Browse Dispose returned before closing captured PreviewStatusChanged handlers.");
        assert(combinedServiceResult.ReentrantReturned
               && combinedBrowseResult.ReentrantReturned
               && combinedServiceResult.ConcurrentWaited
               && combinedBrowseResult.ConcurrentWaited,
            "The combined cancellation/callback Dispose barrier lost reentrancy or concurrent waiting.");
    }

    private static async Task<DisposeReentrancyResult>
        RunServiceCancellationDisposeScenarioAsync()
    {
        var decodeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new ManualResetEventSlim(false);
        var reentrantReturned = new ManualResetEventSlim(false);
        var allowCallbackReturn = new ManualResetEventSlim(false);
        PreviewThumbnailService? service = null;
        var decoder = new ControlledDecoder(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.Set();
                service!.Dispose();
                reentrantReturned.Set();
                allowCallbackReturn.Wait(TimeSpan.FromSeconds(5));
            });
            decodeStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        service = new PreviewThumbnailService(decoder);
        service.SetGeneration(94);
        var lease = service.Acquire(CreateFakeRequest(94, generation: 94));
        await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var owningDispose = Task.Run(service.Dispose);
        var callbackWasEntered = callbackEntered.Wait(TimeSpan.FromSeconds(2));
        var unrelatedDispose = Task.Run(service.Dispose);
        var recursiveReturnedInTime = reentrantReturned.Wait(TimeSpan.FromMilliseconds(750));
        var unrelatedReturnedEarly = await Task.WhenAny(
                                         unrelatedDispose,
                                         Task.Delay(TimeSpan.FromMilliseconds(250)))
                                     == unrelatedDispose;
        if (recursiveReturnedInTime)
        {
            allowCallbackReturn.Set();
            await owningDispose.WaitAsync(TimeSpan.FromSeconds(2));
            await unrelatedDispose.WaitAsync(TimeSpan.FromSeconds(2));
            _ = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            lease.Dispose();
            callbackEntered.Dispose();
            reentrantReturned.Dispose();
            allowCallbackReturn.Dispose();
        }

        return new DisposeReentrancyResult(
            callbackWasEntered && recursiveReturnedInTime,
            callbackWasEntered && !unrelatedReturnedEarly);
    }

    private static async Task<DisposeReentrancyResult>
        RunBrowseCancellationDisposeScenarioAsync()
    {
        var decodeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new ManualResetEventSlim(false);
        var reentrantReturned = new ManualResetEventSlim(false);
        var allowCallbackReturn = new ManualResetEventSlim(false);
        BrowsePageViewModel? browse = null;
        var decoder = new ControlledDecoder(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackEntered.Set();
                browse!.Dispose();
                reentrantReturned.Set();
                allowCallbackReturn.Wait(TimeSpan.FromSeconds(5));
            });
            decodeStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        var service = new PreviewThumbnailService(decoder);
        var problemCenter = new ProblemCenterSession();
        var coordinator = new TaskLifecycleCoordinator();
        var scanSession = new ScanSession(
            new WallpaperScanService(),
            new PathInputValidator(),
            coordinator,
            problemCenter);
        browse = new BrowsePageViewModel(scanSession, problemCenter, service);
        var lease = service.Acquire(CreateFakeRequest(
            95,
            generation: browse.ThumbnailGeneration));
        await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var owningDispose = Task.Run(browse.Dispose);
        var callbackWasEntered = callbackEntered.Wait(TimeSpan.FromSeconds(2));
        var unrelatedDispose = Task.Run(browse.Dispose);
        var recursiveReturnedInTime = reentrantReturned.Wait(TimeSpan.FromMilliseconds(750));
        var unrelatedReturnedEarly = await Task.WhenAny(
                                         unrelatedDispose,
                                         Task.Delay(TimeSpan.FromMilliseconds(250)))
                                     == unrelatedDispose;
        if (recursiveReturnedInTime)
        {
            allowCallbackReturn.Set();
            await owningDispose.WaitAsync(TimeSpan.FromSeconds(2));
            await unrelatedDispose.WaitAsync(TimeSpan.FromSeconds(2));
            _ = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            lease.Dispose();
            callbackEntered.Dispose();
            reentrantReturned.Dispose();
            allowCallbackReturn.Dispose();
        }

        return new DisposeReentrancyResult(
            callbackWasEntered && recursiveReturnedInTime,
            callbackWasEntered && !unrelatedReturnedEarly);
    }

    private static async Task<CombinedDisposeResult>
        RunCombinedServiceDisposeScenarioAsync()
    {
        var activeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var reentrantReturned = new ManualResetEventSlim(false);
        using var allowCancellationReturn = new ManualResetEventSlim(false);
        using var firstHandlerEntered = new ManualResetEventSlim(false);
        using var allowFirstHandlerReturn = new ManualResetEventSlim(false);
        using var firstHandlerReturned = new ManualResetEventSlim(false);
        PreviewThumbnailService? service = null;
        var decoder = new ControlledDecoder(async (request, token) =>
        {
            if (request.SizeBucket == 256)
            {
                await Task.Yield();
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_COMBINED_SERVICE",
                    "组合回调失败。");
            }

            using var registration = token.Register(() =>
            {
                cancellationEntered.Set();
                service!.Dispose();
                reentrantReturned.Set();
                allowCancellationReturn.Wait(TimeSpan.FromSeconds(5));
            });
            activeStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        service = new PreviewThumbnailService(decoder);
        service.SetGeneration(96);
        var secondHandlerCalls = 0;
        service.StatusChanged += (_, _) =>
        {
            firstHandlerEntered.Set();
            allowFirstHandlerReturn.Wait(TimeSpan.FromSeconds(5));
            firstHandlerReturned.Set();
        };
        service.StatusChanged += (_, _) => Interlocked.Increment(
            ref secondHandlerCalls);
        var signalRequest = CreateFakeRequest(96, generation: 96);
        var activeRequest = WithRequestedWidth(signalRequest, 321);
        var activeLease = service.Acquire(activeRequest);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var signalLease = service.Acquire(signalRequest);
        _ = await signalLease.Completion;
        var firstEntered = firstHandlerEntered.Wait(TimeSpan.FromSeconds(2));
        var owningDispose = Task.Run(service.Dispose);
        var cancellationWasEntered = cancellationEntered.Wait(TimeSpan.FromSeconds(2));
        var nestedReturned = reentrantReturned.Wait(TimeSpan.FromSeconds(2));

        allowFirstHandlerReturn.Set();
        var firstReturned = firstHandlerReturned.Wait(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        var laterHandlerSuppressed = Volatile.Read(ref secondHandlerCalls) == 0;

        var unrelatedDispose = Task.Run(service.Dispose);
        var unrelatedReturnedEarly = await Task.WhenAny(
                                         unrelatedDispose,
                                         Task.Delay(TimeSpan.FromMilliseconds(250)))
                                     == unrelatedDispose;
        allowCancellationReturn.Set();
        await owningDispose.WaitAsync(TimeSpan.FromSeconds(2));
        await unrelatedDispose.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await activeLease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        activeLease.Dispose();
        signalLease.Dispose();

        return new CombinedDisposeResult(
            firstEntered && cancellationWasEntered && nestedReturned,
            firstReturned && laterHandlerSuppressed,
            !unrelatedReturnedEarly);
    }

    private static async Task<CombinedDisposeResult>
        RunCombinedBrowseDisposeScenarioAsync()
    {
        var activeStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationEntered = new ManualResetEventSlim(false);
        using var reentrantReturned = new ManualResetEventSlim(false);
        using var allowCancellationReturn = new ManualResetEventSlim(false);
        using var firstHandlerEntered = new ManualResetEventSlim(false);
        using var allowFirstHandlerReturn = new ManualResetEventSlim(false);
        using var firstHandlerReturned = new ManualResetEventSlim(false);
        BrowsePageViewModel? browse = null;
        var decoder = new ControlledDecoder(async (request, token) =>
        {
            if (request.SizeBucket == 256)
            {
                await Task.Yield();
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_COMBINED_BROWSE",
                    "组合浏览回调失败。");
            }

            using var registration = token.Register(() =>
            {
                cancellationEntered.Set();
                browse!.Dispose();
                reentrantReturned.Set();
                allowCancellationReturn.Wait(TimeSpan.FromSeconds(5));
            });
            activeStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        var service = new PreviewThumbnailService(decoder);
        var problemCenter = new ProblemCenterSession();
        var coordinator = new TaskLifecycleCoordinator();
        var scanSession = new ScanSession(
            new WallpaperScanService(),
            new PathInputValidator(),
            coordinator,
            problemCenter);
        browse = new BrowsePageViewModel(scanSession, problemCenter, service);
        var secondHandlerCalls = 0;
        browse.PreviewStatusChanged += (_, _) =>
        {
            firstHandlerEntered.Set();
            allowFirstHandlerReturn.Wait(TimeSpan.FromSeconds(5));
            firstHandlerReturned.Set();
        };
        browse.PreviewStatusChanged += (_, _) => Interlocked.Increment(
            ref secondHandlerCalls);
        var signalRequest = CreateFakeRequest(
            97,
            generation: browse.ThumbnailGeneration);
        var activeRequest = WithRequestedWidth(signalRequest, 321);
        var activeLease = service.Acquire(activeRequest);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var signalLease = service.Acquire(signalRequest);
        _ = await signalLease.Completion;
        var firstEntered = firstHandlerEntered.Wait(TimeSpan.FromSeconds(2));
        var owningDispose = Task.Run(browse.Dispose);
        var cancellationWasEntered = cancellationEntered.Wait(TimeSpan.FromSeconds(2));
        var nestedReturned = reentrantReturned.Wait(TimeSpan.FromSeconds(2));

        allowFirstHandlerReturn.Set();
        var firstReturned = firstHandlerReturned.Wait(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        var laterHandlerSuppressed = Volatile.Read(ref secondHandlerCalls) == 0;

        var unrelatedDispose = Task.Run(browse.Dispose);
        var unrelatedReturnedEarly = await Task.WhenAny(
                                         unrelatedDispose,
                                         Task.Delay(TimeSpan.FromMilliseconds(250)))
                                     == unrelatedDispose;
        allowCancellationReturn.Set();
        await owningDispose.WaitAsync(TimeSpan.FromSeconds(2));
        await unrelatedDispose.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await activeLease.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        activeLease.Dispose();
        signalLease.Dispose();

        return new CombinedDisposeResult(
            firstEntered && cancellationWasEntered && nestedReturned,
            firstReturned && laterHandlerSuppressed,
            !unrelatedReturnedEarly);
    }

    private static async Task VerifyOrderedSignalLifecycleAsync(
        Action<bool, string> assert)
    {
        await VerifyFailureRecoverySignalOrderAsync(assert);
        await VerifyGenerationSignalOrderAsync(assert);
        await VerifySignalDisposalBarrierAsync(assert);
    }

    private static async Task VerifyFailureRecoverySignalOrderAsync(
        Action<bool, string> assert)
    {
        var attempts = 0;
        var decoder = new ControlledDecoder(async (_, _) =>
        {
            await Task.Yield();
            return Interlocked.Increment(ref attempts) == 1
                ? PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_ORDERED_FAILURE",
                    "预览图损坏。")
                : PreviewThumbnailResult.Ready(CreateFrozenBitmap(4, 4));
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(61);
        using var failureEntered = new ManualResetEventSlim(false);
        using var allowFailure = new ManualResetEventSlim(false);
        var observed = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        service.StatusChanged += (_, args) =>
        {
            if (args.Kind == PreviewThumbnailSignalKind.Failed)
            {
                failureEntered.Set();
                allowFailure.Wait(TimeSpan.FromSeconds(5));
            }

            observed.Enqueue(args);
        };

        using var first = service.Acquire(CreateFakeRequest(61, generation: 61));
        _ = await first.Completion;
        await WaitUntilAsync(() => failureEntered.IsSet);
        using var retry = service.Acquire(first.Request, retryFailed: true);
        assert((await retry.Completion).IsSuccess,
            "The ordered signal recovery fixture did not decode successfully.");
        await Task.Delay(100);
        var resolvedEscapedBarrier = observed.Any(signal =>
            signal.Kind == PreviewThumbnailSignalKind.Resolved);
        allowFailure.Set();
        await WaitUntilAsync(() => observed.Count == 2);
        var published = observed.ToArray();
        assert(!resolvedEscapedBarrier
               && published.Select(signal => signal.Kind).SequenceEqual(
                   [PreviewThumbnailSignalKind.Failed, PreviewThumbnailSignalKind.Resolved])
               && GetSignalGeneration(published[0]) == 61
               && GetSignalGeneration(published[1]) == 61
               && GetSignalSequence(published[0]) > 0
               && GetSignalSequence(published[1]) > GetSignalSequence(published[0]),
            "Concurrent failure/recovery publication inverted its linearized signal order or identity.");
    }

    private static async Task VerifyGenerationSignalOrderAsync(
        Action<bool, string> assert)
    {
        var decoder = new ControlledDecoder(async (_, _) =>
        {
            await Task.Yield();
            return PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_GENERATION_FAILURE",
                "预览图损坏。");
        });
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(71);
        using var failureEntered = new ManualResetEventSlim(false);
        using var allowFailure = new ManualResetEventSlim(false);
        var observed = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        service.StatusChanged += (_, args) =>
        {
            if (args.Kind == PreviewThumbnailSignalKind.Failed)
            {
                failureEntered.Set();
                allowFailure.Wait(TimeSpan.FromSeconds(5));
            }

            observed.Enqueue(args);
        };

        using var lease = service.Acquire(CreateFakeRequest(71, generation: 71));
        _ = await lease.Completion;
        await WaitUntilAsync(() => failureEntered.IsSet);
        await Task.Run(() => service.SetGeneration(72));
        await Task.Delay(100);
        var resolvedEscapedBarrier = observed.Any(signal =>
            signal.Kind == PreviewThumbnailSignalKind.Resolved);
        allowFailure.Set();
        await WaitUntilAsync(() => observed.Count == 2);
        var published = observed.ToArray();
        assert(!resolvedEscapedBarrier
               && published.Select(signal => signal.Kind).SequenceEqual(
                   [PreviewThumbnailSignalKind.Failed, PreviewThumbnailSignalKind.Resolved])
               && GetSignalGeneration(published[0]) == 71
               && GetSignalGeneration(published[1]) == 72
               && GetSignalSequence(published[1]) > GetSignalSequence(published[0]),
            "Generation replacement published Resolved before the older Failed callback or lacked stale identity.");
    }

    private static async Task VerifySignalDisposalBarrierAsync(
        Action<bool, string> assert)
    {
        var decoder = new ControlledDecoder(async (_, _) =>
        {
            await Task.Yield();
            return PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_DISPOSE_FAILURE",
                "预览图损坏。");
        });
        var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(81);
        using var firstHandlerEntered = new ManualResetEventSlim(false);
        using var allowFirstHandler = new ManualResetEventSlim(false);
        using var firstHandlerReturned = new ManualResetEventSlim(false);
        var disposeReturned = 0;
        var postDisposeCallbacks = 0;
        service.StatusChanged += (_, _) =>
        {
            firstHandlerEntered.Set();
            allowFirstHandler.Wait(TimeSpan.FromSeconds(5));
            service.Dispose();
            firstHandlerReturned.Set();
        };
        service.StatusChanged += (_, _) =>
        {
            if (Volatile.Read(ref disposeReturned) != 0)
            {
                Interlocked.Increment(ref postDisposeCallbacks);
            }
        };

        using var lease = service.Acquire(CreateFakeRequest(81, generation: 81));
        _ = await lease.Completion;
        await WaitUntilAsync(() => firstHandlerEntered.IsSet);
        var disposeTask = Task.Run(service.Dispose);
        var disposeReturnedBeforeRelease = await Task.WhenAny(
            disposeTask,
            Task.Delay(TimeSpan.FromSeconds(1))) == disposeTask;
        if (disposeReturnedBeforeRelease)
        {
            Volatile.Write(ref disposeReturned, 1);
        }

        allowFirstHandler.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref disposeReturned, 1);
        await WaitUntilAsync(() => firstHandlerReturned.IsSet);
        await Task.Delay(50);
        assert(!disposeReturnedBeforeRelease && postDisposeCallbacks == 0,
            "Service Dispose did not wait for an active callback or allowed a captured callback afterward.");

        var selfDecoder = new ControlledDecoder(async (_, _) =>
        {
            await Task.Yield();
            return PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_SELF_DISPOSE",
                "预览图损坏。");
        });
        var selfService = new PreviewThumbnailService(selfDecoder);
        selfService.SetGeneration(82);
        var selfDisposeReturned = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandlerCalls = 0;
        selfService.StatusChanged += (_, _) =>
        {
            selfService.Dispose();
            selfDisposeReturned.TrySetResult(true);
        };
        selfService.StatusChanged += (_, _) => Interlocked.Increment(
            ref secondHandlerCalls);
        using var selfLease = selfService.Acquire(CreateFakeRequest(82, generation: 82));
        _ = await selfLease.Completion;
        await selfDisposeReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        assert(secondHandlerCalls == 0,
            "A handler-initiated Service Dispose deadlocked or allowed the next handler to start.");
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

        (int Item, int Pixel) viewportCounts = default;
        await RunOnStaAsync(async () =>
        {
            var itemCount = await MeasureVirtualizedViewportAcquisitionsAsync(
                ScrollUnit.Item);
            var pixelCount = await MeasureVirtualizedViewportAcquisitionsAsync(
                ScrollUnit.Pixel);
            viewportCounts = (itemCount, pixelCount);
        });
        Console.WriteLine(
            $"PREVIEW_VIEWPORT_COUNTS item={viewportCounts.Item} pixel={viewportCounts.Pixel}");
        assert(viewportCounts.Item == 3 && viewportCounts.Pixel == 3,
            "Virtualized Item/Pixel scrolling did not acquire exactly the visible rows plus one-row overscan.");
    }

    private static async Task<int> MeasureVirtualizedViewportAcquisitionsAsync(
        ScrollUnit scrollUnit)
    {
        var decoder = new ControlledDecoder((_, _) => Task.FromResult(
            PreviewThumbnailResult.Ready(CreateFrozenBitmap(16, 10))));
        using var service = new PreviewThumbnailService(decoder);
        service.SetGeneration(scrollUnit == ScrollUnit.Item ? 91 : 92);
        var generation = service.Generation;
        var list = new ListBox
        {
            Width = 240,
            Height = 202,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var containerStyle = new Style(typeof(ListBoxItem));
        containerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 100d));
        containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        containerStyle.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        list.ItemContainerStyle = containerStyle;
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(1));
        VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Item);
        VirtualizingPanel.SetScrollUnit(list, scrollUnit);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        foreach (var index in Enumerable.Range(0, 8))
        {
            list.Items.Add(new ThumbnailPreviewImage
            {
                Height = 100,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ThumbnailService = service,
                ProjectKey = $"virtual-{scrollUnit}-{index}",
                SourcePath = Path.Combine(
                    Path.GetTempPath(),
                    $"virtual-{scrollUnit}-{index}.png"),
                ScanFileLength = 100 + index,
                ScanLastWriteTimeUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
                PreviewFormat = ".png",
                SnapshotGeneration = generation,
                DecodePixelWidth = 193
            });
        }

        var window = new Window
        {
            Width = 260,
            Height = 240,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            SizeToContent = SizeToContent.Manual,
            Content = list
        };
        try
        {
            window.Show();
            await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
            await WaitUntilAsync(
                () => decoder.CallCount >= 2,
                dispatcher: window.Dispatcher);
            await Task.Delay(100);
            await DrainAsync(window.Dispatcher, DispatcherPriority.ApplicationIdle);
            return decoder.CallCount;
        }
        finally
        {
            window.Close();
        }
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

    private static async Task VerifyBrowseSignalAffinityAsync(
        Action<bool, string> assert)
    {
        await RunOnStaAsync(async () =>
        {
            var ownerThreadId = Environment.CurrentManagedThreadId;
            var shell = WallpaperField.Composition.AppComposition.CreateShellViewModel();
            var affinityCorrect = false;
            try
            {
                var browse = shell.BrowsePageViewModel;
                var published = new TaskCompletionSource<(int ThreadId,
                    PreviewThumbnailSignalEventArgs Signal)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                browse.PreviewStatusChanged += (_, args) => published.TrySetResult(
                    (Environment.CurrentManagedThreadId, args));
                var request = CreateMissingRequest(
                    "browse-affinity",
                    browse.ThumbnailGeneration);
                using var lease = browse.ThumbnailService.Acquire(request);
                _ = await lease.Completion;
                await WaitUntilAsync(
                    () => published.Task.IsCompleted,
                    dispatcher: Dispatcher.CurrentDispatcher);
                var observed = await published.Task;
                affinityCorrect = observed.ThreadId == ownerThreadId
                                  && GetSignalGeneration(observed.Signal)
                                      == browse.ThumbnailGeneration
                                  && GetSignalSequence(observed.Signal) > 0;
            }
            finally
            {
                shell.Dispose();
            }

            var disposeShell = WallpaperField.Composition.AppComposition.CreateShellViewModel();
            var postDisposeCallbacks = 0;
            var serviceSignalReached = new ManualResetEventSlim(false);
            try
            {
                var browse = disposeShell.BrowsePageViewModel;
                browse.PreviewStatusChanged += (_, _) =>
                    Interlocked.Increment(ref postDisposeCallbacks);
                browse.ThumbnailService.StatusChanged += (_, _) =>
                    serviceSignalReached.Set();
                var request = CreateMissingRequest(
                    "browse-dispose",
                    browse.ThumbnailGeneration);
                using var lease = browse.ThumbnailService.Acquire(request);
                var workerReachedServiceSeam = serviceSignalReached.Wait(
                    TimeSpan.FromSeconds(2));
                browse.Dispose();
                await DrainAsync(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle);
                affinityCorrect = affinityCorrect
                                  && workerReachedServiceSeam
                                  && Volatile.Read(ref postDisposeCallbacks) == 0;
            }
            finally
            {
                serviceSignalReached.Dispose();
                disposeShell.Dispose();
            }

            assert(affinityCorrect,
                "Browse preview signals were not marshalled to their owner or escaped after disposal.");
        });
    }

    private static async Task VerifyBrowseDisposeCallbackBarrierAsync(
        Action<bool, string> assert)
    {
        var selfDisposeSafe = false;
        var concurrentDisposeSafe = false;
        await RunOnStaAsync(async () =>
        {
            var selfShell = WallpaperField.Composition.AppComposition
                .CreateShellViewModel();
            try
            {
                var browse = selfShell.BrowsePageViewModel;
                var firstReturned = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var secondCalls = 0;
                browse.PreviewStatusChanged += (_, _) =>
                {
                    browse.Dispose();
                    firstReturned.TrySetResult(true);
                };
                browse.PreviewStatusChanged += (_, _) => Interlocked.Increment(
                    ref secondCalls);
                using var lease = browse.ThumbnailService.Acquire(
                    CreateMissingRequest("browse-self-dispose", browse.ThumbnailGeneration));
                _ = await lease.Completion;
                await WaitUntilAsync(
                    () => firstReturned.Task.IsCompleted,
                    dispatcher: Dispatcher.CurrentDispatcher);
                await DrainAsync(
                    Dispatcher.CurrentDispatcher,
                    DispatcherPriority.ApplicationIdle);
                selfDisposeSafe = secondCalls == 0;
            }
            finally
            {
                selfShell.Dispose();
            }

            var concurrentShell = WallpaperField.Composition.AppComposition
                .CreateShellViewModel();
            using var callbackEntered = new ManualResetEventSlim(false);
            using var allowCallback = new ManualResetEventSlim(false);
            using var disposeReturned = new ManualResetEventSlim(false);
            try
            {
                var browse = concurrentShell.BrowsePageViewModel;
                var secondCalls = 0;
                browse.PreviewStatusChanged += (_, _) =>
                {
                    callbackEntered.Set();
                    allowCallback.Wait(TimeSpan.FromSeconds(5));
                    browse.Dispose();
                };
                browse.PreviewStatusChanged += (_, _) => Interlocked.Increment(
                    ref secondCalls);
                var disposeTask = Task.Run(() =>
                {
                    callbackEntered.Wait(TimeSpan.FromSeconds(2));
                    browse.Dispose();
                    disposeReturned.Set();
                });
                var releaseTask = Task.Run(() =>
                {
                    callbackEntered.Wait(TimeSpan.FromSeconds(2));
                    var returnedEarly = disposeReturned.Wait(TimeSpan.FromMilliseconds(250));
                    allowCallback.Set();
                    return returnedEarly;
                });
                using var lease = browse.ThumbnailService.Acquire(
                    CreateMissingRequest("browse-concurrent-dispose", browse.ThumbnailGeneration));
                _ = await lease.Completion;
                var returnedBeforeRelease = await releaseTask.WaitAsync(
                    TimeSpan.FromSeconds(3));
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(3));
                await DrainAsync(
                    Dispatcher.CurrentDispatcher,
                    DispatcherPriority.ApplicationIdle);
                concurrentDisposeSafe = !returnedBeforeRelease && secondCalls == 0;
            }
            finally
            {
                allowCallback.Set();
                concurrentShell.Dispose();
            }
        });

        assert(selfDisposeSafe && concurrentDisposeSafe,
            "Browse Dispose deadlocked/released early or allowed a later captured callback to start.");
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

    private static void EqualizeFileEnvelope(string firstPath, string secondPath)
    {
        var first = File.ReadAllBytes(firstPath);
        var second = File.ReadAllBytes(secondPath);
        var length = Math.Max(first.Length, second.Length);
        Array.Resize(ref first, length);
        Array.Resize(ref second, length);
        File.WriteAllBytes(firstPath, first);
        File.WriteAllBytes(secondPath, second);
        var timestamp = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(firstPath, timestamp);
        File.SetLastWriteTimeUtc(secondPath, timestamp);
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

    private static PreviewThumbnailRequest WithProjectKey(
        PreviewThumbnailRequest request,
        string projectKey)
        => new(
            projectKey,
            request.CanonicalPath,
            request.ScanFileLength,
            request.ScanLastWriteTimeUtc,
            request.PreviewFormat,
            request.SizeBucket,
            request.Generation);

    private static PreviewThumbnailRequest WithRequestedWidth(
        PreviewThumbnailRequest request,
        int requestedPixelWidth)
        => new(
            request.ProjectKey,
            request.CanonicalPath,
            request.ScanFileLength,
            request.ScanLastWriteTimeUtc,
            request.PreviewFormat,
            requestedPixelWidth,
            request.Generation);

    private static PreviewThumbnailRequest CreateMissingRequest(
        string projectKey,
        long generation)
        => new(
            projectKey,
            Path.Combine(
                Path.GetTempPath(),
                $"WallpaperField-Missing-{Guid.NewGuid():N}.png"),
            1,
            DateTimeOffset.UnixEpoch,
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

    private static BitmapSource CreateFrozenRgba64Bitmap(int width, int height)
    {
        var stride = checked(width * 8);
        var pixels = new byte[checked(stride * height)];
        for (var index = 0; index < pixels.Length; index += 8)
        {
            pixels[index] = 0xFF;
            pixels[index + 1] = 0x3F;
            pixels[index + 2] = 0xFF;
            pixels[index + 3] = 0x7F;
            pixels[index + 4] = 0xFF;
            pixels[index + 5] = 0xBF;
            pixels[index + 6] = 0xFF;
            pixels[index + 7] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Rgba64,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static long CalculateAlignedBitmapBytes(BitmapSource bitmap)
    {
        var rowBits = checked((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel);
        var stride = checked(((rowBits + 31) / 32) * 4);
        return checked(stride * bitmap.PixelHeight);
    }

    private static long GetSignalGeneration(PreviewThumbnailSignalEventArgs args)
        => args.GetType().GetProperty("Generation")?.GetValue(args) is long generation
            ? generation
            : -1;

    private static long GetSignalSequence(PreviewThumbnailSignalEventArgs args)
        => args.GetType().GetProperty("Sequence")?.GetValue(args) is long sequence
            ? sequence
            : -1;

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

    private static async Task<bool> WaitForWeakOwnersCollectionAsync(
        WeakReference<byte[]>? encodedOwner,
        WeakReference<BitmapSource>? sourceOwner)
    {
        if (encodedOwner is null || sourceOwner is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (!encodedOwner.TryGetTarget(out _)
                && !sourceOwner.TryGetTarget(out _))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
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

    private readonly record struct DisposeReentrancyResult(
        bool ReentrantReturned,
        bool ConcurrentWaited);

    private readonly record struct CombinedDisposeResult(
        bool ReentrantReturned,
        bool LaterHandlerSuppressed,
        bool ConcurrentWaited);

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

        internal string WriteBitmap(
            string relativePath,
            BitmapEncoder encoder,
            BitmapSource bitmap)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
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
