using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class PreviewThumbnailService : IDisposable
{
    private readonly object _sync = new();
    private readonly IPreviewThumbnailDecoder _decoder;
    private readonly SemaphoreSlim _decodeGate = new(
        PreviewThumbnailLimits.MaximumConcurrentDecodes,
        PreviewThumbnailLimits.MaximumConcurrentDecodes);
    private readonly Dictionary<PreviewThumbnailCacheKey, DecodeOperation> _operations = [];
    private readonly Dictionary<PreviewThumbnailCacheKey, CacheEntry> _cache = [];
    private readonly Dictionary<FailureIdentity, FailureDetails> _openFailures = [];
    private readonly Dictionary<FailureIdentity, long> _recoveryEpochs = [];
    private readonly Queue<PreviewThumbnailSignalEventArgs> _pendingSignals = [];
    private readonly ReentrantCallbackGate<PreviewThumbnailSignalEventArgs>
        _statusCallbacks = new();
    private readonly TaskCompletionSource<bool> _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _disposed;
    private bool _signalPublisherActive;
    private long _generation;
    private long _signalSequence;
    private long _accessOrdinal;
    private int _activeDecodes;
    private int _peakActiveDecodes;
    private long _cacheDecodedBytes;
    private long _decodeRequestCount;
    private long _inFlightShareCount;
    private long _cacheHitCount;
    private long _failureCacheHitCount;
    private long _cancellationCount;
    private long _staleDiscardCount;
    private long _evictionCount;

    public PreviewThumbnailService(IPreviewThumbnailDecoder decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
    }

    public event EventHandler<PreviewThumbnailSignalEventArgs>? StatusChanged
    {
        add => _statusCallbacks.Add(value);
        remove => _statusCallbacks.Remove(value);
    }

    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public void SetGeneration(long generation)
    {
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        List<CancellationTokenSource>? cancellations = null;
        List<PreviewThumbnailSignalEventArgs>? signals = null;

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_generation == generation)
            {
                return;
            }

            _generation = generation;
            ClearCacheLocked();

            foreach (var operation in _operations.Values)
            {
                operation.Retired = true;
                if (!operation.Completed)
                {
                    _staleDiscardCount++;
                    operation.Completion.TrySetResult(PreviewThumbnailResult.Stale());
                    (cancellations ??= []).Add(operation.Cancellation);
                }
            }

            _operations.Clear();

            foreach (var (identity, details) in _openFailures)
            {
                (signals ??= []).Add(CreateSignal(
                    PreviewThumbnailSignalKind.Resolved,
                    identity,
                    details));
            }

            _openFailures.Clear();
            _recoveryEpochs.Clear();
            EnqueueSignalsLocked(signals);
        }

        CancelAll(cancellations);
        DrainSignals();
    }

    public PreviewThumbnailLease Acquire(
        PreviewThumbnailRequest request,
        bool retryFailed = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        DecodeOperation? operationToStart = null;
        List<PreviewThumbnailSignalEventArgs>? signals = null;
        PreviewThumbnailLease lease;

        lock (_sync)
        {
            ThrowIfDisposed();
            if (request.Generation != _generation)
            {
                _staleDiscardCount++;
                return PreviewThumbnailLease.Completed(
                    request,
                    PreviewThumbnailResult.Stale());
            }

            var key = request.CreateCacheKey();
            var identity = FailureIdentity.From(request);

            if (_operations.TryGetValue(key, out var existing))
            {
                if (retryFailed
                    && existing.Completed
                    && existing.Result?.IsStableFailure == true)
                {
                    existing.Retired = true;
                    _operations.Remove(key);
                }
                else
                {
                    existing.ObserverCount++;
                    var addedIdentity = existing.AddFailureIdentity(
                        identity,
                        GetRecoveryEpochLocked(identity));
                    _inFlightShareCount++;
                    if (addedIdentity && existing.Completed && existing.Result is not null)
                    {
                        if (existing.Result.IsSuccess)
                        {
                            RecoverFailureStateLocked(
                                [identity],
                                existing);
                        }

                        CollectSignalsLocked(
                            existing.Result,
                            [identity],
                            ref signals);
                    }

                    lease = CreateLease(request, existing);
                    goto CompleteAcquire;
                }
            }

            if (_cache.Remove(key, out var cached))
            {
                _cacheDecodedBytes = checked(
                    _cacheDecodedBytes - cached.Result.DecodedBytes);
                if (!retryFailed || !cached.Result.IsStableFailure)
                {
                    _cacheHitCount++;
                    if (cached.Result.IsStableFailure)
                    {
                        _failureCacheHitCount++;
                    }

                    var completed = DecodeOperation.CreateCompleted(
                        key,
                        request,
                        identity,
                        GetRecoveryEpochLocked(identity),
                        cached.Result,
                        cached.FailureIdentities.Select(cachedIdentity =>
                            new KeyValuePair<FailureIdentity, long>(
                                cachedIdentity,
                                GetRecoveryEpochLocked(cachedIdentity))));
                    _operations.Add(key, completed);
                    if (cached.Result.IsSuccess)
                    {
                        RecoverFailureStateLocked(
                            completed.FailureIdentities,
                            completed);
                    }

                    CollectSignalsLocked(
                        cached.Result,
                        completed.FailureIdentities,
                        ref signals);
                    lease = CreateLease(request, completed);
                    goto CompleteAcquire;
                }
            }

            operationToStart = new DecodeOperation(
                key,
                request,
                identity,
                GetRecoveryEpochLocked(identity));
            _operations.Add(key, operationToStart);
            _decodeRequestCount++;
            lease = CreateLease(request, operationToStart);

        CompleteAcquire:
            EnqueueSignalsLocked(signals);
        }

        DrainSignals();
        if (operationToStart is not null)
        {
            _ = RunDecodeAsync(operationToStart);
        }

        return lease;
    }

    public PreviewThumbnailMetrics GetMetrics()
    {
        lock (_sync)
        {
            return new PreviewThumbnailMetrics(
                _activeDecodes,
                _peakActiveDecodes,
                _operations.Values.Count(operation => !operation.Completed),
                _operations.Values.Sum(operation => operation.ObserverCount),
                _cache.Count,
                _cacheDecodedBytes,
                _decodeRequestCount,
                _inFlightShareCount,
                _cacheHitCount,
                _failureCacheHitCount,
                _cancellationCount,
                _staleDiscardCount,
                _evictionCount);
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource>? cancellations = null;
        var alreadyDisposed = false;

        lock (_sync)
        {
            if (_disposed)
            {
                alreadyDisposed = true;
            }
            else
            {
                _disposed = true;
                foreach (var operation in _operations.Values)
                {
                    operation.Retired = true;
                    if (!operation.Completed)
                    {
                        operation.Completion.TrySetResult(PreviewThumbnailResult.Cancelled());
                        (cancellations ??= []).Add(operation.Cancellation);
                    }
                }

                _operations.Clear();
                ClearCacheLocked();
                _openFailures.Clear();
                _recoveryEpochs.Clear();
                _pendingSignals.Clear();
            }
        }

        if (alreadyDisposed)
        {
            _statusCallbacks.Dispose();
            if (!_statusCallbacks.IsActiveOnCurrentThread)
            {
                _disposeCompletion.Task.GetAwaiter().GetResult();
            }

            return;
        }

        try
        {
            CancelAll(cancellations);
        }
        finally
        {
            _statusCallbacks.Dispose();
            _disposeCompletion.TrySetResult(true);
        }
    }

    private PreviewThumbnailLease CreateLease(
        PreviewThumbnailRequest request,
        DecodeOperation operation)
        => new(request, operation.Completion.Task, () => Release(operation));

    private void Release(DecodeOperation operation)
    {
        CancellationTokenSource? cancellation = null;

        lock (_sync)
        {
            if (operation.Retired || operation.ObserverCount == 0)
            {
                return;
            }

            operation.ObserverCount--;
            if (operation.ObserverCount != 0)
            {
                return;
            }

            operation.Retired = true;
            if (_operations.TryGetValue(operation.Key, out var current)
                && ReferenceEquals(current, operation))
            {
                _operations.Remove(operation.Key);
            }

            if (!operation.Completed)
            {
                _cancellationCount++;
                operation.Completion.TrySetResult(PreviewThumbnailResult.Cancelled());
                cancellation = operation.Cancellation;
            }
            else if (operation.Result is { } result && IsCacheable(result))
            {
                AddToCacheLocked(
                    operation.Key,
                    result,
                    operation.FailureIdentities);
            }
        }

        TryCancel(cancellation);
    }

    private async Task RunDecodeAsync(DecodeOperation operation)
    {
        var enteredGate = false;
        var countedActive = false;
        PreviewThumbnailResult? result = null;
        Exception? fatalException = null;

        try
        {
            await _decodeGate.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            enteredGate = true;

            lock (_sync)
            {
                if (!operation.Retired)
                {
                    _activeDecodes++;
                    countedActive = true;
                    _peakActiveDecodes = Math.Max(_peakActiveDecodes, _activeDecodes);
                }
            }

            if (!countedActive)
            {
                result = PreviewThumbnailResult.Cancelled();
            }
            else
            {
                result = await _decoder.DecodeAsync(
                    operation.Request.CreateDecodeRequest(),
                    operation.Cancellation.Token).ConfigureAwait(false);
                result = NormalizeResult(result);
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            result = PreviewThumbnailResult.Cancelled();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            result = PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_DECODE_FAILED",
                "预览图无法安全解码。");
        }
        catch (Exception exception) when (IsFatal(exception))
        {
            fatalException = exception;
        }
        finally
        {
            if (countedActive)
            {
                lock (_sync)
                {
                    _activeDecodes--;
                }
            }

            if (enteredGate)
            {
                _decodeGate.Release();
            }
        }

        try
        {
            if (fatalException is not null)
            {
                CompleteOperationFault(operation, fatalException);
            }
            else
            {
                CompleteOperation(operation, result!);
            }
        }
        finally
        {
            operation.Cancellation.Dispose();
        }
    }

    private void CompleteOperationFault(
        DecodeOperation operation,
        Exception exception)
    {
        lock (_sync)
        {
            if (operation.Retired
                || !_operations.TryGetValue(operation.Key, out var current)
                || !ReferenceEquals(current, operation))
            {
                return;
            }

            operation.Completed = true;
            operation.Completion.TrySetException(exception);
        }
    }

    private void CompleteOperation(
        DecodeOperation operation,
        PreviewThumbnailResult result)
    {
        List<PreviewThumbnailSignalEventArgs>? signals = null;

        lock (_sync)
        {
            if (operation.Retired
                || !_operations.TryGetValue(operation.Key, out var current)
                || !ReferenceEquals(current, operation))
            {
                return;
            }

            if (operation.Request.Generation != _generation)
            {
                operation.Retired = true;
                _operations.Remove(operation.Key);
                _staleDiscardCount++;
                operation.Completion.TrySetResult(PreviewThumbnailResult.Stale());
                return;
            }

            if (result.IsStableFailure && RecoveredSinceStartLocked(operation))
            {
                _staleDiscardCount++;
                result = PreviewThumbnailResult.Stale();
            }

            operation.Completed = true;
            operation.Result = result;
            if (result.IsSuccess)
            {
                RecoverFailureStateLocked(
                    operation.FailureIdentities,
                    operation);
            }

            CollectSignalsLocked(result, operation.FailureIdentities, ref signals);
            operation.Completion.TrySetResult(result);

            if (operation.ObserverCount == 0)
            {
                operation.Retired = true;
                _operations.Remove(operation.Key);
                if (IsCacheable(result))
                {
                    AddToCacheLocked(
                        operation.Key,
                        result,
                        operation.FailureIdentities);
                }
            }

            EnqueueSignalsLocked(signals);
        }

        DrainSignals();
    }

    private void CollectSignalsLocked(
        PreviewThumbnailResult result,
        IEnumerable<FailureIdentity> identities,
        ref List<PreviewThumbnailSignalEventArgs>? signals)
    {
        foreach (var identity in identities)
        {
            if (result.IsStableFailure)
            {
                var details = new FailureDetails(
                    result.FailureCode ?? "PREVIEW_DECODE_FAILED",
                    result.FailureSummary ?? "预览图无法安全解码。");
                if (_openFailures.TryAdd(identity, details))
                {
                    (signals ??= []).Add(CreateSignal(
                        PreviewThumbnailSignalKind.Failed,
                        identity,
                        details));
                }
            }
            else if (result.IsSuccess
                     && _openFailures.Remove(identity, out var details))
            {
                (signals ??= []).Add(CreateSignal(
                    PreviewThumbnailSignalKind.Resolved,
                    identity,
                    details));
            }
        }
    }

    private PreviewThumbnailSignalEventArgs CreateSignal(
        PreviewThumbnailSignalKind kind,
        FailureIdentity identity,
        FailureDetails details)
        => new(
            kind,
            identity.ProjectKey,
            identity.PreviewVersion,
            details.Code,
            details.Summary,
            _generation,
            checked(++_signalSequence));

    private void AddToCacheLocked(
        PreviewThumbnailCacheKey key,
        PreviewThumbnailResult result,
        IEnumerable<FailureIdentity> failureIdentities)
    {
        if (_cache.Remove(key, out var replaced))
        {
            _cacheDecodedBytes = checked(
                _cacheDecodedBytes - replaced.Result.DecodedBytes);
        }

        _cache.Add(
            key,
            new CacheEntry(
                result,
                ++_accessOrdinal,
                failureIdentities.Distinct().ToArray()));
        _cacheDecodedBytes = checked(_cacheDecodedBytes + result.DecodedBytes);

        while (_cache.Count > PreviewThumbnailLimits.MaximumEntries
               || _cacheDecodedBytes > PreviewThumbnailLimits.MaximumDecodedCacheBytes)
        {
            var oldest = _cache.MinBy(pair => pair.Value.AccessOrdinal);
            if (!_cache.Remove(oldest.Key, out var evicted))
            {
                break;
            }

            _cacheDecodedBytes = checked(_cacheDecodedBytes - evicted.Result.DecodedBytes);
            _evictionCount++;
        }
    }

    private void RecoverFailureStateLocked(
        IEnumerable<FailureIdentity> recoveredIdentities,
        DecodeOperation except)
    {
        var recovered = recoveredIdentities.ToHashSet();
        if (recovered.Count == 0)
        {
            return;
        }

        foreach (var identity in recovered)
        {
            _recoveryEpochs[identity] = checked(GetRecoveryEpochLocked(identity) + 1);
        }

        foreach (var (key, entry) in _cache.ToArray())
        {
            if (!entry.Result.IsStableFailure
                || !entry.FailureIdentities.Any(recovered.Contains)
                || !_cache.Remove(key, out var removed))
            {
                continue;
            }

            _cacheDecodedBytes = checked(
                _cacheDecodedBytes - removed.Result.DecodedBytes);
        }

        foreach (var (key, operation) in _operations.ToArray())
        {
            if (ReferenceEquals(operation, except)
                || !operation.FailureIdentities.Any(recovered.Contains)
                || !operation.Completed
                || operation.Result?.IsStableFailure != true)
            {
                continue;
            }

            operation.Retired = true;
            if (_operations.TryGetValue(key, out var current)
                && ReferenceEquals(current, operation))
            {
                _operations.Remove(key);
            }

        }
    }

    private bool RecoveredSinceStartLocked(DecodeOperation operation)
        => operation.FailureEpochs.Any(pair =>
            GetRecoveryEpochLocked(pair.Key) > pair.Value);

    private long GetRecoveryEpochLocked(FailureIdentity identity)
        => _recoveryEpochs.GetValueOrDefault(identity);

    private void ClearCacheLocked()
    {
        _cache.Clear();
        _cacheDecodedBytes = 0;
    }

    private static PreviewThumbnailResult NormalizeResult(PreviewThumbnailResult? result)
    {
        if (result is null)
        {
            return PreviewThumbnailResult.Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_DECODE_FAILED",
                "预览图无法安全解码。");
        }

        if (result.Status == PreviewThumbnailStatus.Ready)
        {
            if (result.Bitmap is null || !result.Bitmap.IsFrozen)
            {
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_INVALID_RESULT",
                    "预览图解码结果无效。");
            }

            try
            {
                if (result.Bitmap.PixelWidth > PreviewThumbnailLimits.MaximumDimension
                    || result.Bitmap.PixelHeight > PreviewThumbnailLimits.MaximumDimension)
                {
                    return PreviewThumbnailResult.Failure(
                        PreviewThumbnailStatus.OverBudget,
                        "PREVIEW_DIMENSION",
                        "预览图边长超过 4096 像素预算。");
                }

                PreviewThumbnailLimits.ValidateSourcePixelCount(
                    checked((long)result.Bitmap.PixelWidth * result.Bitmap.PixelHeight));
                var actualBytes = PreviewThumbnailLimits.CalculateDecodedBytes(result.Bitmap);
                if (actualBytes > PreviewThumbnailLimits.MaximumDecodedCacheBytes)
                {
                    return PreviewThumbnailResult.Failure(
                        PreviewThumbnailStatus.OverBudget,
                        "PREVIEW_DECODED_BYTES",
                        "预览图解码结果超过内存预算。");
                }

                return new PreviewThumbnailResult(
                    PreviewThumbnailStatus.Ready,
                    result.Bitmap,
                    actualBytes,
                    null,
                    null);
            }
            catch (Exception exception) when (exception is
                       PreviewThumbnailBudgetException or OverflowException)
            {
                return PreviewThumbnailResult.Failure(
                    PreviewThumbnailStatus.OverBudget,
                    "PREVIEW_DECODED_BYTES",
                    "预览图解码结果超过内存预算。");
            }
        }

        if (result.IsStableFailure)
        {
            return PreviewThumbnailResult.Failure(
                Enum.IsDefined(result.Status)
                    ? result.Status
                    : PreviewThumbnailStatus.Corrupt,
                string.IsNullOrWhiteSpace(result.FailureCode)
                    ? "PREVIEW_DECODE_FAILED"
                    : result.FailureCode,
                string.IsNullOrWhiteSpace(result.FailureSummary)
                    ? "预览图无法安全解码。"
                    : result.FailureSummary);
        }

        return result.Status == PreviewThumbnailStatus.Stale
            ? PreviewThumbnailResult.Stale()
            : PreviewThumbnailResult.Cancelled();
    }

    private static bool IsCacheable(PreviewThumbnailResult result)
        => result.IsSuccess || result.IsStableFailure;

    private void EnqueueSignalsLocked(
        List<PreviewThumbnailSignalEventArgs>? signals)
    {
        if (signals is null)
        {
            return;
        }

        foreach (var signal in signals)
        {
            _pendingSignals.Enqueue(signal);
        }
    }

    private void DrainSignals()
    {
        lock (_sync)
        {
            if (_disposed || _signalPublisherActive || _pendingSignals.Count == 0)
            {
                return;
            }

            _signalPublisherActive = true;
        }

        var restart = false;
        try
        {
            while (true)
            {
                PreviewThumbnailSignalEventArgs signal;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        _pendingSignals.Clear();
                        return;
                    }

                    if (!_pendingSignals.TryDequeue(out signal!))
                    {
                        return;
                    }

                }

                _statusCallbacks.Invoke(
                    this,
                    signal,
                    static exception => !IsFatal(exception));
            }
        }
        finally
        {
            lock (_sync)
            {
                _signalPublisherActive = false;
                restart = !_disposed && _pendingSignals.Count > 0;
            }

            if (restart)
            {
                DrainSignals();
            }
        }
    }

    private static void CancelAll(List<CancellationTokenSource>? cancellations)
    {
        if (cancellations is null)
        {
            return;
        }

        foreach (var cancellation in cancellations)
        {
            TryCancel(cancellation);
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private sealed class DecodeOperation
    {
        internal DecodeOperation(
            PreviewThumbnailCacheKey key,
            PreviewThumbnailRequest request,
            FailureIdentity identity,
            long recoveryEpoch)
        {
            Key = key;
            Request = request;
            FailureIdentities = [identity];
            FailureEpochs = new Dictionary<FailureIdentity, long>
            {
                [identity] = recoveryEpoch
            };
        }

        internal PreviewThumbnailCacheKey Key { get; }

        internal PreviewThumbnailRequest Request { get; }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource<PreviewThumbnailResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HashSet<FailureIdentity> FailureIdentities { get; }

        internal Dictionary<FailureIdentity, long> FailureEpochs { get; }

        internal bool AddFailureIdentity(FailureIdentity identity, long recoveryEpoch)
        {
            if (!FailureIdentities.Add(identity))
            {
                return false;
            }

            FailureEpochs.Add(identity, recoveryEpoch);
            return true;
        }

        internal int ObserverCount { get; set; } = 1;

        internal bool Completed { get; set; }

        internal bool Retired { get; set; }

        internal PreviewThumbnailResult? Result { get; set; }

        internal static DecodeOperation CreateCompleted(
            PreviewThumbnailCacheKey key,
            PreviewThumbnailRequest request,
            FailureIdentity identity,
            long recoveryEpoch,
            PreviewThumbnailResult result,
            IEnumerable<KeyValuePair<FailureIdentity, long>> cachedFailureEpochs)
        {
            var operation = new DecodeOperation(
                key,
                request,
                identity,
                recoveryEpoch)
            {
                Completed = true,
                Result = result
            };
            foreach (var cached in cachedFailureEpochs)
            {
                operation.AddFailureIdentity(cached.Key, cached.Value);
            }

            operation.Completion.TrySetResult(result);
            operation.Cancellation.Dispose();
            return operation;
        }
    }

    private sealed record CacheEntry(
        PreviewThumbnailResult Result,
        long AccessOrdinal,
        IReadOnlyList<FailureIdentity> FailureIdentities);

    private readonly record struct FailureIdentity(
        string ProjectKey,
        string PreviewVersion)
    {
        internal static FailureIdentity From(PreviewThumbnailRequest request)
            => new(request.ProjectKey, request.PreviewVersion);
    }

    private readonly record struct FailureDetails(string Code, string Summary);
}

public sealed class PreviewThumbnailLease : IDisposable
{
    private Action? _release;

    internal PreviewThumbnailLease(
        PreviewThumbnailRequest request,
        Task<PreviewThumbnailResult> completion,
        Action? release)
    {
        Request = request;
        Completion = completion;
        _release = release;
    }

    public PreviewThumbnailRequest Request { get; }

    public Task<PreviewThumbnailResult> Completion { get; }

    internal static PreviewThumbnailLease Completed(
        PreviewThumbnailRequest request,
        PreviewThumbnailResult result)
        => new(request, Task.FromResult(result), null);

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed class ReentrantCallbackGate<TEventArgs> : IDisposable
    where TEventArgs : EventArgs
{
    private readonly object _sync = new();
    private readonly Dictionary<int, int> _activeCallbacksByThread = [];
    private EventHandler<TEventArgs>? _handlers;
    private bool _disposed;
    private int _activeCallbacks;

    internal bool IsActiveOnCurrentThread
    {
        get
        {
            lock (_sync)
            {
                return _activeCallbacksByThread.ContainsKey(
                    Environment.CurrentManagedThreadId);
            }
        }
    }

    internal void Add(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_sync)
        {
            if (!_disposed)
            {
                _handlers += handler;
            }
        }
    }

    internal void Remove(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_sync)
        {
            _handlers -= handler;
        }
    }

    internal void Invoke(
        object sender,
        TEventArgs args,
        Func<Exception, bool>? suppressException = null)
    {
        Delegate[] snapshot;
        lock (_sync)
        {
            if (_disposed || _handlers is null)
            {
                return;
            }

            snapshot = _handlers.GetInvocationList();
        }

        foreach (EventHandler<TEventArgs> handler in snapshot)
        {
            if (!TryBeginCallback(handler))
            {
                continue;
            }

            try
            {
                handler(sender, args);
            }
            catch (Exception exception) when (suppressException?.Invoke(exception) == true)
            {
                // The owner decides which observer failures are safe to isolate.
            }
            finally
            {
                EndCallback();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _handlers = null;
            if (_activeCallbacksByThread.ContainsKey(Environment.CurrentManagedThreadId))
            {
                return;
            }

            while (_activeCallbacks > 0)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    private bool TryBeginCallback(EventHandler<TEventArgs> handler)
    {
        lock (_sync)
        {
            if (_disposed
                || _handlers is null
                || !_handlers.GetInvocationList().Contains(handler))
            {
                return false;
            }

            _activeCallbacks++;
            var threadId = Environment.CurrentManagedThreadId;
            _activeCallbacksByThread[threadId] =
                _activeCallbacksByThread.GetValueOrDefault(threadId) + 1;
            return true;
        }
    }

    private void EndCallback()
    {
        lock (_sync)
        {
            _activeCallbacks--;
            var threadId = Environment.CurrentManagedThreadId;
            var threadCallbacks = _activeCallbacksByThread[threadId] - 1;
            if (threadCallbacks == 0)
            {
                _activeCallbacksByThread.Remove(threadId);
            }
            else
            {
                _activeCallbacksByThread[threadId] = threadCallbacks;
            }

            if (_activeCallbacks == 0)
            {
                Monitor.PulseAll(_sync);
            }
        }
    }
}
