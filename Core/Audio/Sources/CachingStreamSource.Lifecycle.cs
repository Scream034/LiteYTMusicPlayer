namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- Section: Epoch-Based Cancellation ---

    /// <summary>
    /// Откладывает <see cref="IDisposable.Dispose"/> для CTS,
    /// предотвращая <see cref="ObjectDisposedException"/> в конкурентных путях.
    /// </summary>
    private static void DeferDisposeCancellationTokenSource(
        CancellationTokenSource? cts, int delayMs)
    {
        if (cts == null) return;

        ThreadPool.UnsafeQueueUserWorkItem(static async state =>
        {
            var (source, delay) = ((CancellationTokenSource Source, int DelayMs))state!;
            try { await Task.Delay(delay).ConfigureAwait(false); } catch { }
            try { source.Dispose(); } catch (ObjectDisposedException) { }
        }, (cts, delayMs));
    }

    /// <summary>Отменяет все загрузки текущей эпохи и создаёт новую.</summary>
    private CancellationToken ResetDownloadEpoch()
    {
        lock (_epochLock)
        {
            var oldCts = _downloadCts;

            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();

            Interlocked.Increment(ref _downloadEpoch);

            if (oldCts != null)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                }, oldCts);

                DeferDisposeCancellationTokenSource(oldCts, DeferredEpochDisposeDelayMs);
            }

            return _downloadCts.Token;
        }
    }

    /// <summary>Инициализирует первую эпоху загрузки.</summary>
    private void InitializeFirstEpoch()
    {
        lock (_epochLock)
        {
            _downloadCts = _lifetimeCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token)
                : new CancellationTokenSource();
            _downloadEpoch = 1;
        }
    }

    /// <summary>CancellationToken текущей эпохи загрузки. Потокобезопасно.</summary>
    private CancellationToken CurrentDownloadToken
    {
        get
        {
            lock (_epochLock)
                return _downloadCts?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>Мгновенно отменяет активные чтения на потоке без уничтожения источника.</summary>
    public void CancelActiveReads() => _readStream?.CancelActiveReads();

    // --- Section: Dispose ---

    /// <summary>Общая преамбула dispose: разблокировка gates, cancel epoch + lifetime.</summary>
    private void BeginDispose()
    {
        _suspendGate.Set();
        _playbackGate.Set();

        CancellationTokenSource? downloadCtsToDispose;

        lock (_epochLock)
        {
            downloadCtsToDispose = _downloadCts;
            _downloadCts = null;
        }

        if (downloadCtsToDispose != null)
        {
            try { downloadCtsToDispose.Cancel(); }
            catch (ObjectDisposedException) { }

            DeferDisposeCancellationTokenSource(downloadCtsToDispose, DeferredEpochDisposeDelayMs);
        }

        try { _lifetimeCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Общий эпилог dispose: освобождение всех ресурсов.</summary>
    private void DisposeSharedResources()
    {
        try { _lifetimeCts?.Dispose(); } catch (ObjectDisposedException) { }

        _readStream?.Dispose();
        DisposeAllRamChunks();

        lock (_continuationLock)
        {
            var tcs = _continuationUrlTcs;
            _continuationUrlTcs = null;
            tcs?.TrySetResult(null);
        }

        try { _refreshLock.Dispose(); } catch (ObjectDisposedException) { }
        try { _downloadSlots.Dispose(); } catch (ObjectDisposedException) { }

        _suspendGate.Dispose();
        _playbackGate.Dispose();

        if (_leaseAcquired)
            _cacheManager.ReleaseLease(_cacheKey);
    }

    /// <summary>Диспозит все блоки в RAM-кэше.</summary>
    private void DisposeAllRamChunks() => _ramCache.DisposeAll();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();
        _parser?.Dispose();
        DrainPendingDiskWritesSync();
        DisposeSharedResources();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        BeginDispose();

        if (_preloadTask != null)
        {
            try
            {
                await _preloadTask
                    .WaitAsync(TimeSpan.FromMilliseconds(PreloadTaskDisposeWaitTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch { }
        }

        await DrainPendingDiskWritesAsync().ConfigureAwait(false);
        await Task.Delay(DisposalDelayMs).ConfigureAwait(false);

        if (_parser != null)
            await _parser.DisposeAsync().ConfigureAwait(false);

        DisposeSharedResources();
    }

    // --- Section: Drain ---

    /// <summary>
    /// Ожидает завершения всех фоновых disk-write операций перед освобождением lease.
    /// </summary>
    private async Task DrainPendingDiskWritesAsync()
    {
        const int maxWaitMs = 2000;
        const int pollIntervalMs = 25;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            elapsed += pollIntervalMs;
        }

        int remaining = Volatile.Read(ref _pendingDiskWrites);
        if (remaining > 0)
            Log.Warn(
                $"[CachingSource] {remaining} pending disk writes not drained within {maxWaitMs}ms");
    }

    /// <summary>Sync fallback для дренажа pending writes.</summary>
    private void DrainPendingDiskWritesSync()
    {
        const int maxWaitMs = 500;
        const int pollIntervalMs = 10;
        int elapsed = 0;

        while (Volatile.Read(ref _pendingDiskWrites) > 0 && elapsed < maxWaitMs)
        {
            Thread.Sleep(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }
}