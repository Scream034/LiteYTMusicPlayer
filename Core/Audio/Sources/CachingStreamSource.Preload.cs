using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Suspend/Resume

    /// <summary>
    /// Приостанавливает фоновую загрузку (при сворачивании окна).
    /// Critical read-ahead продолжает работать для бесперебойного воспроизведения.
    /// </summary>
    public void Suspend()
    {
        _suspendGate.Reset();
        Log.Debug("[CachingSource] Suspended (background fill paused, critical read-ahead active)");
    }

    /// <summary>
    /// Возобновляет фоновую загрузку.
    /// </summary>
    public void Resume()
    {
        _suspendGate.Set();
        Log.Debug("[CachingSource] Resumed");
    }

    #endregion

    #region Preload Loop

    /// <summary>
    /// Фоновый цикл предзагрузки диапазонов.
    /// <para>
    /// Первая итерация выполняется без задержки, чтобы немедленно подхватить
    /// работу <see cref="SafeStartupPrefetchAsync"/> и продолжить заполнение буфера.
    /// Последующие итерации разделены интервалом <see cref="StreamingConfig.PreloadIntervalMs"/>.
    /// </para>
    /// <para>
    /// Адаптирует параллелизм и размер запросов к состоянию сети.
    /// Включает opportunistic completion: если трек почти скачан,
    /// добиваем оставшиеся gaps до полного кэширования.
    /// </para>
    /// </summary>
    private async Task PreloadLoopAsync(CancellationToken ct)
    {
        bool justResumed = false;

        /// <summary>
        /// Порог "почти скачан" — если скачано >= этого процента, включается completion fill.
        /// </summary>
        const double CompletionFillThresholdPercent = 90.0;

        /// <summary>
        /// Максимальный абсолютный размер недокачки, при котором включается completion fill.
        /// </summary>
        const long CompletionFillMaxRemainingBytes = 512 * 1024;

#if DEBUG
        int lastReportedProgress = -1;
#endif

        while (!ct.IsCancellationRequested && _cacheEntry is { IsComplete: false })
        {
            try
            {
                bool isSuspended = !_suspendGate.IsSet;
                bool isPaused = !_playbackGate.IsSet;

                //  Пауза: ждём открытия gate 
                if (isPaused)
                {
                    try { _playbackGate.Wait(PlaybackGateTimeoutMs, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                //  Suspend mode: только critical read-ahead 
                if (isSuspended)
                {
                    long current = Volatile.Read(ref _currentReadOffset);
                    long bufferedAhead = GetBufferedBytesAheadIncludingInflight(current);

                    if (bufferedAhead < _config.MinRequestSizeBytes)
                    {
                        long nextPos = current + bufferedAhead;
                        int nextLen = GetAlignedReadLength(nextPos, _config.MinRequestSizeBytes);

                        try
                        {
                            await EnsureRangeAsync(nextPos, nextLen, ct, isCritical: true)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ChunkDownloadFatalException ex)
                        {
                            if (ex.Reason is ChunkDownloadFailureReason.UmpFormat
                                          or ChunkDownloadFailureReason.Forbidden403)
                            {
                                Log.Warn($"[CachingSource] Permanent preload fatal in suspend: {ex.Reason}");
                                break;
                            }

                            Log.Debug($"[CachingSource] Transient preload fatal in suspend, " +
                                      $"backing off: {ex.Message}");
                            try { await Task.Delay(3000, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { break; }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"[CachingSource] Suspended critical preload: {ex.Message}");
                        }
                    }

                    try { _suspendGate.Wait(PlaybackGateCriticalTimeoutMs, ct); }
                    catch (OperationCanceledException) { break; }

                    if (_suspendGate.IsSet)
                        justResumed = true;

                    continue;
                }

                // Критическая сетевая операция seek в процессе — уступаем ей весь канал
                if (_seekInProgress)
                {
                    try { await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                //  Дебаунс после resume 
                if (justResumed)
                {
                    justResumed = false;
                    try { await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (_cacheEntry.IsComplete) break;

                //  Snapshot текущего состояния 
                long epochAtLoopStart = Interlocked.Read(ref _downloadEpoch);
                var token = CurrentDownloadToken;
                long currentOffset = Volatile.Read(ref _currentReadOffset);

                int adaptiveMaxDownloads = GetAdaptiveMaxConcurrentDownloads();
                int pending = GetActiveDownloadCount();

                if (pending >= adaptiveMaxDownloads)
                {
                    await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
                int refillStopMs = adaptiveTargetBufferMs + TargetBufferHysteresisMs;

                var degradation = GetNetworkDegradationLevel();

                //  Prefetch loop: планируем запросы вперёд от текущей позиции 
                while (pending < adaptiveMaxDownloads)
                {
                    if (Interlocked.Read(ref _downloadEpoch) != epochAtLoopStart)
                        break;

                    long plannedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentOffset);
                    int plannedAheadMs = ConvertBufferedBytesToMs(plannedAheadBytes);

                    if (plannedAheadMs >= refillStopMs)
                        break;

                    long nextPosition = currentOffset + plannedAheadBytes;
                    if (nextPosition >= _contentLength)
                        break;

                    int requestBytes = GetAlignedReadLength(nextPosition, _config.MinRequestSizeBytes);
                    if (requestBytes <= 0)
                        break;

                    bool critical =
                        plannedAheadMs < EmergencyRefillBufferMs
                        || (pending == 0 && plannedAheadMs < CriticalRefillBufferMs);

                    _ = SafeEnsureRangeAsync(nextPosition, requestBytes, token, critical);
                    pending++;

                    if (degradation != NetworkDegradationLevel.Normal)
                        break;

                    if (!critical)
                        break;
                }

                //  Opportunistic completion fill 
                if (pending < adaptiveMaxDownloads && !_cacheEntry.IsComplete)
                {
                    long downloadedBytes = _cacheEntry.DownloadedBytes;
                    long totalSize = _cacheEntry.TotalSize;
                    long remainingBytes = totalSize - downloadedBytes;

                    bool shouldCompletionFill = totalSize > 0
                        && ((_cacheEntry.DownloadProgress >= CompletionFillThresholdPercent)
                            || (remainingBytes > 0 && remainingBytes <= CompletionFillMaxRemainingBytes));

                    if (shouldCompletionFill)
                    {
                        await TryCompletionFillAsync(token, adaptiveMaxDownloads - pending)
                            .ConfigureAwait(false);
                    }
                }

                //  Trim RAM cache 
                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    ReleaseRamBuffers();

#if DEBUG
                int progress = (int)(_cacheEntry?.DownloadProgress ?? 0);
                if (progress != lastReportedProgress)
                {
                    Log.Debug($"[CachingSource] Progress: {progress}% " +
                              $"({_cacheEntry?.DownloadedBytes ?? 0}/{_cacheEntry?.TotalSize ?? 0} bytes)");
                    lastReportedProgress = progress;
                }
#endif

                //  Интервал ожидания перед следующей итерацией 
                // Delay в конце цикла, а не в начале:
                // первая итерация выполняется немедленно после startup,
                // что устраняет мёртвое время PreloadIntervalMs между init и первым preload-запросом.
                await Task.Delay(_config.PreloadIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* Защита от неожиданных исключений в фоновом цикле */ }
        }
    }

    /// <summary>
    /// Сканирует все скачанные диапазоны и заполняет первые найденные gaps.
    /// </summary>
    /// <param name="downloadToken">Токен текущей эпохи загрузки.</param>
    /// <param name="maxRequests">Максимальное количество одновременных запросов.</param>
    private async Task TryCompletionFillAsync(
        CancellationToken downloadToken,
        int maxRequests)
    {
        if (_cacheEntry == null || _cacheEntry.IsComplete || maxRequests <= 0)
            return;

        long totalSize = _cacheEntry.TotalSize;
        if (totalSize <= 0) return;

        var ranges = _cacheEntry.GetDownloadedRangesSnapshot();
        int fired = 0;

        long scanPosition = 0;

        for (int i = 0; i <= ranges.Length && fired < maxRequests; i++)
        {
            long gapStart;
            long gapEnd;

            if (i < ranges.Length)
            {
                gapStart = scanPosition;
                gapEnd = ranges[i].Start;
                scanPosition = ranges[i].EndExclusive;
            }
            else
            {
                gapStart = scanPosition;
                gapEnd = totalSize;
            }

            if (gapEnd <= gapStart)
                continue;

            long gapLength = gapEnd - gapStart;
            if (gapLength <= 0)
                continue;

            int requestLength = (int)Math.Min(gapLength, _config.MaxRequestSizeBytes);

            _ = SafeEnsureRangeAsync(gapStart, requestLength, downloadToken, isCritical: false);
            fired++;
        }

        if (fired > 0)
        {
            Log.Debug($"[CachingSource] Completion fill: {fired} gap request(s) fired " +
                      $"({_cacheEntry.DownloadProgress:F0}% → 100%)");
        }
    }

    /// <summary>
    /// Безопасная обёртка над <see cref="EnsureRangeAsync"/> для fire-and-forget вызовов.
    /// </summary>
    private async Task SafeEnsureRangeAsync(
        long position, int minimumLength, CancellationToken ct, bool isCritical)
    {
        try
        {
            await EnsureRangeAsync(position, minimumLength, ct, isCritical).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChunkDownloadFatalException ex)
        {
            Log.Debug($"[Preload] Range {position}: fatal: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Preload] Range {position}: {ex.Message}");
        }
    }

    #endregion

    #region Buffer Ranges

    /// <inheritdoc/>
    /// <remarks>
    /// Объединяет диапазоны из disk-кэша и RAM-кэша,
    /// нормализует в <c>[0.0, 1.0]</c> относительно <see cref="_contentLength"/>.
    /// </remarks>
    public IReadOnlyList<(double Start, double End)> GetBufferedRanges()
    {
        if (_cacheEntry == null || _contentLength <= 0)
            return [];

        var diskRanges = _cacheEntry.GetDownloadedRangesSnapshot();
        var ramBlocks = _ramCache.GetRangesSnapshot();

        if (diskRanges.Length == 0 && ramBlocks.Length == 0)
            return [];

        var all = new List<(long Start, long End)>(diskRanges.Length + ramBlocks.Length);

        for (int i = 0; i < diskRanges.Length; i++)
            all.Add((diskRanges[i].Start, diskRanges[i].EndExclusive));

        for (int i = 0; i < ramBlocks.Length; i++)
            all.Add((ramBlocks[i].StartOffset, ramBlocks[i].EndOffsetExclusive));

        all.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(double, double)>(all.Count);
        long mergedStart = -1;
        long mergedEnd = -1;

        for (int i = 0; i < all.Count; i++)
        {
            var (s, e) = all[i];

            if (mergedStart < 0)
            {
                mergedStart = s;
                mergedEnd = e;
                continue;
            }

            if (s <= mergedEnd)
            {
                if (e > mergedEnd)
                    mergedEnd = e;
            }
            else
            {
                merged.Add(((double)mergedStart / _contentLength, (double)mergedEnd / _contentLength));
                mergedStart = s;
                mergedEnd = e;
            }
        }

        if (mergedStart >= 0)
            merged.Add(((double)mergedStart / _contentLength, (double)mergedEnd / _contentLength));

        return merged;
    }

    /// <summary>
    /// Обновляет URL потока через <see cref="_urlRefresher"/> при истечении 403.
    /// </summary>
    private async Task RefreshUrlAsync(CancellationToken ct)
    {
        if (_urlRefresher == null) return;

        try
        {
            var newUrl = await _urlRefresher(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(newUrl))
            {
                _currentUrl = newUrl;
                Log.Info("[CachingSource] URL refreshed");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] URL refresh failed: {ex.Message}");
        }
    }

    #endregion
}