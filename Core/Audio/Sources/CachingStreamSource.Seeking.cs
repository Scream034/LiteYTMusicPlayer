namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <inheritdoc/>
    public async ValueTask<bool> SeekAsync(long positionMs, CancellationToken ct)
    {
        if (_parser == null) return false;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return false;

        long targetBytePos = seekInfo.Value.BytePosition;
        long segmentStartMs = seekInfo.Value.TimestampMs;

        if (targetBytePos >= _contentLength)
        {
            Log.Warn($"[CachingSource] Seek position {targetBytePos} >= contentLength {_contentLength}, clamping to EOF");
            targetBytePos = Math.Max(SeekLowerBound, _contentLength - SeekEndOffset);
        }

        Log.Debug($"[CachingSource] Seek: {positionMs}ms → byte {targetBytePos}");

        // 1. Перемещение позиции потока и отмена pending reads
        _readStream!.SeekAndCancelPendingReads(targetBytePos);
        Volatile.Write(ref _currentReadOffset, targetBytePos);

        // 2. Сброс парсера для чтения с новой позиции
        _parser.Reset();
        Volatile.Write(ref _positionMs, segmentStartMs);

        // 3. Быстрый путь: данные уже есть локально
        if (HasMinimalLocalSeekStartData(targetBytePos))
        {
            Log.Debug($"[CachingSource] Seek: sufficient local prefix at {targetBytePos}, starting immediately");
            _ = PreloadRangeForSeekFireAndForgetAsync(targetBytePos, skipBytes: 0);
            return true;
        }

        // 4. Медленный путь: данных нет, нужна сетевая загрузка
        _seekInProgress = true;
        int minimalBytes;
        try
        {
            ResetDownloadEpoch();

            minimalBytes = GetMinimalSeekStartBytes(targetBytePos);
            if (minimalBytes <= 0)
                return true;

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);
                linkedCts.CancelAfter(ComputeAdaptiveSeekCriticalTimeoutMs(minimalBytes));

                Log.Debug($"[CachingSource] Seek: downloading minimal {minimalBytes} bytes at {targetBytePos}");

                await EnsureRangeAsync(targetBytePos, minimalBytes, linkedCts.Token, isCritical: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    return false;

                Log.Debug("[CachingSource] Seek: critical download timed out, decoder will block until data arrives");
            }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] Seek: critical range failed: {ex.Message}");
            }
        }
        finally
        {
            _seekInProgress = false;
        }

        // 5. Запуск фоновой предзагрузки (R2)
        long committedAhead = GetBufferedBytesAhead(targetBytePos);
        int r2SkipBytes = (int)Math.Min(committedAhead, int.MaxValue);

        Log.Debug($"[CachingSource] Seek: R1 committed {r2SkipBytes} bytes (requested {minimalBytes}), R2 starts at offset {r2SkipBytes}");

        _ = PreloadRangeForSeekFireAndForgetAsync(targetBytePos, skipBytes: r2SkipBytes);

        return true;
    }

    /// <summary>
    /// Best-effort prefetch минимального startup prefix для предстоящего seek.
    /// Вызывается из <c>AudioPlayer</c> ПАРАЛЛЕЛЬНО с остановкой decoder,
    /// до вызова <see cref="SeekAsync"/>.
    /// </summary>
    /// <param name="positionMs">Целевая позиция seek в миллисекундах.</param>
    /// <param name="ct">Токен отмены (lifetime, НЕ epoch).</param>
    internal async Task TryPrefetchChunkForSeekAsync(long positionMs, CancellationToken ct)
    {
        if (_cacheEntry == null || _parser == null) return;

        var seekInfo = _parser.FindSeekPosition(positionMs);
        if (seekInfo == null) return;

        long targetBytePos = Math.Min(seekInfo.Value.BytePosition, Math.Max(0, _contentLength - 1));

        if (HasMinimalLocalSeekStartData(targetBytePos))
            return;

        int prefetchLength = GetMinimalSeekStartBytes(targetBytePos);
        if (prefetchLength <= 0) return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ComputeAdaptiveSeekCriticalTimeoutMs(prefetchLength));

            await EnsureRangeAsync(targetBytePos, prefetchLength, timeoutCts.Token, isCritical: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Prefetch range {targetBytePos}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fire-and-forget preload диапазона вокруг seek-позиции.
    /// Не блокирует возврат из <see cref="SeekAsync"/>.
    /// </summary>
    /// <param name="targetBytePos">Базовая позиция перемотки.</param>
    /// <param name="skipBytes">Количество байт, уже запрошенных синхронным критическим путём.</param>
    private async Task PreloadRangeForSeekFireAndForgetAsync(long targetBytePos, int skipBytes)
    {
        try
        {
            long epochAtStart = Interlocked.Read(ref _downloadEpoch);
            var downloadToken = CurrentDownloadToken;
            int preloadLength = GetAdaptiveSeekPreloadBytes();

            // Сдвигаем стартовую позицию, чтобы не запрашивать то, что уже качается в SeekAsync
            long startPos = targetBytePos + skipBytes;
            int remainingPreload = preloadLength - skipBytes;

            if (startPos >= _contentLength || remainingPreload <= 0)
                return;

            if (remainingPreload > _contentLength - startPos)
                remainingPreload = (int)(_contentLength - startPos);

            if (Interlocked.Read(ref _downloadEpoch) != epochAtStart)
                return;

            if (!IsRangeLocallyAvailable(startPos, remainingPreload))
            {
                Log.Debug($"[CachingSource] Seek preloading next range at {startPos}, bytes={remainingPreload}");

                await EnsureRangeAsync(startPos, remainingPreload, downloadToken, isCritical: false)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug($"[CachingSource] Seek preload error: {ex.Message}");
        }
    }
}