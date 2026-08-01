using System.Buffers;
using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    #region Nested Types

    private enum RangeDownloadResult
    {
        Success, Forbidden403, NetworkError, Fatal, Cancelled, SlotTimeout, OutOfRange
    }

    internal sealed class RamRangeBlock : IDisposable
    {
        private IMemoryOwner<byte>? _owner;
        private int _disposed;
        public long StartOffset { get; }
        public long EndOffsetExclusive => StartOffset + Length;
        public int Length { get; }
        public Memory<byte> Memory { get; }

        public RamRangeBlock(long startOffset, IMemoryOwner<byte> owner, int actualLength)
        {
            StartOffset = startOffset;
            _owner = owner;
            Length = actualLength;
            Memory = owner.Memory[..actualLength];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner?.Dispose();
            _owner = null;
        }
    }

    internal sealed class SlidingRamCache : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<RamRangeBlock> _blocks = new(32);
        private long _totalBytes;

        public long TotalBytes
        {
            get { lock (_lock) return _totalBytes; }
        }

        public bool TryAdd(RamRangeBlock block)
        {
            lock (_lock)
            {
                int insertAt = _blocks.Count;
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive <= block.StartOffset) continue;
                    if (block.EndOffsetExclusive <= current.StartOffset) { insertAt = i; break; }
                    return false;
                }
                _blocks.Insert(insertAt, block);
                _totalBytes += block.Length;
                return true;
            }
        }

        public bool TryRead(long position, Memory<byte> destination, out int read)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    int offsetInBlock = (int)(position - current.StartOffset);
                    read = Math.Min(destination.Length, current.Length - offsetInBlock);
                    if (read <= 0) { read = 0; return false; }
                    current.Memory.Span.Slice(offsetInBlock, read).CopyTo(destination.Span);
                    return true;
                }
            }
            read = 0; return false;
        }

        public bool ContainsRange(long startOffset, int length)
        {
            long endExclusive = startOffset + length;
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > startOffset) break;
                    if (current.EndOffsetExclusive <= startOffset) continue;
                    return current.StartOffset <= startOffset && current.EndOffsetExclusive >= endExclusive;
                }
            }
            return false;
        }

        public long GetContiguousBytesFrom(long position)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    return current.EndOffsetExclusive - position;
                }
            }
            return 0;
        }

        public bool TryRemoveContaining(long position, out RamRangeBlock? block)
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var current = _blocks[i];
                    if (current.StartOffset > position) break;
                    if (position >= current.EndOffsetExclusive) continue;
                    _blocks.RemoveAt(i);
                    _totalBytes -= current.Length;
                    block = current;
                    return true;
                }
            }
            block = null; return false;
        }

        public RamRangeBlock[] GetRangesSnapshot()
        {
            lock (_lock) { return _blocks.Count == 0 ? [] : [.. _blocks]; }
        }

        public void Trim(long centerOffset, long evictionWindowBytes, long maxRamBytes)
        {
            lock (_lock)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--)
                {
                    var current = _blocks[i];
                    if (current.EndOffsetExclusive < centerOffset - evictionWindowBytes || current.StartOffset > centerOffset + evictionWindowBytes)
                    {
                        _blocks.RemoveAt(i);
                        _totalBytes -= current.Length;
                        current.Dispose();
                    }
                }
                while (_totalBytes > maxRamBytes && _blocks.Count > 0)
                {
                    int removeIndex = ChooseFarthestIndex(centerOffset);
                    var removed = _blocks[removeIndex];
                    _blocks.RemoveAt(removeIndex);
                    _totalBytes -= removed.Length;
                    removed.Dispose();
                }
            }
        }

        public void DisposeAll()
        {
            lock (_lock)
            {
                for (int i = 0; i < _blocks.Count; i++) _blocks[i].Dispose();
                _blocks.Clear();
                _totalBytes = 0;
            }
        }

        public void Dispose() => DisposeAll();

        private int ChooseFarthestIndex(long centerOffset)
        {
            int chosen = 0;
            long farthestDistance = long.MinValue;
            for (int i = 0; i < _blocks.Count; i++)
            {
                long blockCenter = _blocks[i].StartOffset + (_blocks[i].Length >> 1);
                long distance = blockCenter >= centerOffset ? blockCenter - centerOffset : centerOffset - blockCenter;
                if (distance > farthestDistance) { farthestDistance = distance; chosen = i; }
            }
            return chosen;
        }
    }

    private sealed class ActiveRangeDownload
    {
        public long Start { get; }
        public int Length { get; }
        public long EndExclusive => Start + Length;
        public Lazy<Task<RangeDownloadResult>> LazyTask { get; }

        public ActiveRangeDownload(long start, int length, Lazy<Task<RangeDownloadResult>> lazyTask)
        {
            Start = start;
            Length = length;
            LazyTask = lazyTask;
        }
    }

    private readonly record struct DownloadPlan(long Start, int Length);

    #endregion

    #region Adaptive Metrics

    private void SaveLatency(double latencyMs)
    {
        double currentAverage;
        lock (_latencyLock)
        {
            _latency2 = _latency1; _latency1 = _latency0; _latency0 = latencyMs;
            currentAverage = GetAverageLatencyInternal();
        }
        Log.Debug($"[CachingSource] Latency: {latencyMs:F1}ms (Average Trend: {currentAverage:F1}ms)");
    }

    /// <summary>
    /// Сохраняет замер пропускной способности через взвешенное скользящее среднее (EMA).
    /// <para>
    /// Алгоритм работает в двух фазах:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Bootstrap-фаза</b> (первые <see cref="StreamingConfig.BandwidthBootstrapSampleCount"/>
    ///     замеров): применяется фиксированный повышенный вес
    ///     (<see cref="StreamingConfig.BandwidthBootstrapWeight"/>).
    ///     Обеспечивает быструю сходимость EMA на startup и после seek,
    ///     когда предыдущая оценка отсутствует или устарела.
    ///   </item>
    ///   <item>
    ///     <b>Steady-state фаза</b>: вес пропорционален объёму переданных данных —
    ///     от 5% для мелких блоков (TCP slow-start, кэши ОС) до 50% для крупных (≥ 256 KB).
    ///     Сглаживает случайные всплески скорости от коротких чанков.
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="speedBytesPerSec">Измеренная скорость в байт/сек.</param>
    /// <param name="bytesTransferred">Объём переданных данных, определяющий вес замера.</param>
    private void SaveBandwidth(double speedBytesPerSec, int bytesTransferred)
    {
        double currentSpeed;
        lock (_latencyLock)
        {
            if (_estimatedBandwidthBytesPerSec <= 0)
            {
                // Первый замер: инициализируем EMA напрямую без усреднения.
                _estimatedBandwidthBytesPerSec = speedBytesPerSec;
                _bandwidthSampleCount = 1;
            }
            else
            {
                _bandwidthSampleCount++;

                double weight;
                if (_bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount)
                {
                    // Bootstrap-фаза: фиксированный высокий вес для быстрого выхода
                    // на реальную оценку канала независимо от размера блока.
                    weight = _config.BandwidthBootstrapWeight;
                }
                else
                {
                    // Steady-state: вес зависит от объёма переданных данных.
                    // Крупные блоки (≥ 256 KB) несут больше информации о реальной скорости,
                    // мелкие блоки (< 16 KB) могут отражать TCP slow-start или кэш ОС.
                    weight = Math.Clamp(
                        bytesTransferred / (double)(256 * 1024),
                        min: 0.05,
                        max: 0.50);
                }

                _estimatedBandwidthBytesPerSec =
                    (weight * speedBytesPerSec) + ((1.0 - weight) * _estimatedBandwidthBytesPerSec);
            }

            currentSpeed = _estimatedBandwidthBytesPerSec;
        }

        double speedMbps = currentSpeed * 8.0 / 1_000_000.0;
        Log.Debug(
            $"[CachingSource] Throughput: {speedMbps:F2} Mbps ({currentSpeed / 1024.0:F1} KB/s, " +
            $"sample={_bandwidthSampleCount}, " +
            $"phase={(_bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount
                      ? "bootstrap"
                      : "steady")})");
    }

    private double GetAverageLatencyInternal()
    {
        if (_latency0 <= 0) return 0;
        if (_latency1 <= 0) return _latency0;
        if (_latency2 <= 0) return (_latency0 + _latency1) / 2.0;
        return (_latency0 + _latency1 + _latency2) / 3.0;
    }

    private double GetThrottleTargetBytesPerSec()
    {
        double multiplier = _config.ThrottleMultiplier;
        if (multiplier <= 0) return 0;
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return bitrateBytesPerSec * multiplier;
    }

    #endregion

    #region Range Planning

    /// <summary>
    /// Вычисляет оптимальный диапазон для следующего HTTP range-запроса (MAPO planner).
    /// <para>
    /// Алгоритм работает в два режима в зависимости от состояния буфера:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Startup/seek фаза</b> (<c>currentBufferSec &lt; BdpFloorMaxBufferMs</c>):
    ///     активируется BDP floor — запрашиваем не менее одного Bandwidth-Delay Product,
    ///     чтобы максимально загрузить канал и быстро наполнить буфер.
    ///   </item>
    ///   <item>
    ///     <b>Steady-state фаза</b> (буфер достаточен): чистый demand-based sizing
    ///     по модели BBA (Buffer-Based Adaptation). BDP floor не применяется —
    ///     достаточно качать ровно столько, сколько нужно для поддержания
    ///     <see cref="StreamingConfig.TargetBufferMs"/>.
    ///   </item>
    /// </list>
    /// <para>
    /// Ко всем bandwidth-based оценкам применяется
    /// <see cref="StreamingConfig.ThroughputSafetyFactor"/> (≤ 1.0) по модели
    /// THROUGHPUT (dash.js) — резервируем часть канала, чтобы не упираться в его потолок.
    /// </para>
    /// </summary>
    private DownloadPlan BuildDownloadPlan(long requestedPosition, int minimumLength, bool isCritical)
    {
        long start = AlignDown(requestedPosition, _requestAlignmentBytes);

        long localAvailable = GetBufferedBytesAhead(start);
        if (localAvailable > 0)
        {
            long adjustedStart = AlignUp(start + localAvailable, _requestAlignmentBytes);
            if (adjustedStart < _contentLength && adjustedStart <= requestedPosition + minimumLength)
                start = adjustedStart;
        }

        int minLengthAligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);

        long currentPosition = Volatile.Read(ref _currentReadOffset);
        if (currentPosition < 0 || currentPosition >= _contentLength)
            currentPosition = requestedPosition;

        long bufferedAheadBytes = GetBufferedBytesAheadIncludingInflight(currentPosition);
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        double currentBufferSec = bufferedAheadBytes / bitrateBytesPerSec;

        int adaptiveTargetBufferMs = GetAdaptiveTargetBufferMs();
        double targetBufferSec = adaptiveTargetBufferMs / 1000.0;
        double demandBytes = bitrateBytesPerSec * Math.Max(0, targetBufferSec - currentBufferSec);

        double avgLatencyMs;
        double estimatedBandwidth;
        lock (_latencyLock)
        {
            avgLatencyMs = GetAverageLatencyInternal();
            estimatedBandwidth = _estimatedBandwidthBytesPerSec;
        }

        var degradation = GetNetworkDegradationLevel();
        int effectiveMaxLength = _config.MaxRequestSizeBytes;

        if (!isCritical)
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 2),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                _ => effectiveMaxLength
            };
        }
        else
        {
            effectiveMaxLength = degradation switch
            {
                NetworkDegradationLevel.Critical => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 4),
                NetworkDegradationLevel.Degraded => Math.Min(effectiveMaxLength, _requestAlignmentBytes * 6),
                _ => effectiveMaxLength
            };
        }

        double throttleBps = GetThrottleTargetBytesPerSec();
        if (throttleBps > 0)
        {
            double pacingWindow = Math.Max(1.0, _config.PreloadIntervalMs / 1000.0);
            int throttleCap = AlignUp((int)(throttleBps * pacingWindow), _requestAlignmentBytes);
            effectiveMaxLength = Math.Min(throttleCap, effectiveMaxLength);
        }

        int maxLength = Math.Max(minLengthAligned, effectiveMaxLength);

        // Throughput safety factor (модель THROUGHPUT, dash.js)
        // Используем ThroughputSafetyFactor от оценённой пропускной способности,
        // чтобы не упираться в потолок канала и оставлять запас для других запросов.
        // На узком канале (10 Мбит/с) без этого фактора safeBytes = 100% bandwidth,
        // что приводит к конкуренции за канал между R1 (seek) и R2 (preload).
        double safeBytes = 0;
        if (estimatedBandwidth > 0)
        {
            double effectiveBandwidth = estimatedBandwidth * _config.ThroughputSafetyFactor;
            double timeLeftSec = Math.Max(0.050, currentBufferSec - (avgLatencyMs / 1000.0));
            safeBytes = effectiveBandwidth * timeLeftSec;
        }

        long selectedBytes;
        if (safeBytes > 0 && demandBytes > 0) selectedBytes = (long)Math.Min(demandBytes, safeBytes);
        else if (demandBytes > 0) selectedBytes = (long)demandBytes;
        else if (safeBytes > 0) selectedBytes = (long)safeBytes;
        else selectedBytes = minLengthAligned;

        if (selectedBytes < minLengthAligned) selectedBytes = minLengthAligned;

        // BDP floor (только в startup/seek фазе)
        // По BBA-принципу (Netflix/dash.js): в steady state буфер уже достаточен,
        // оценка пропускной способности не нужна — demand-based sizing справляется.
        // BDP floor активируем только при пустом буфере, когда нужно максимально
        // загрузить канал за один RTT, чтобы быстро наполнить буфер.
        bool isStartupOrSeekPhase = currentBufferSec < (_config.BdpFloorMaxBufferMs / 1000.0);
        if (estimatedBandwidth > 0 && avgLatencyMs > 600 && isStartupOrSeekPhase)
        {
            double latencySec = avgLatencyMs / 1000.0;
            // BDP × safety factor: не претендуем на 100% трубы даже при заполнении буфера.
            long bdpBytes = (long)(estimatedBandwidth * _config.ThroughputSafetyFactor * latencySec);
            long bdpFloor = AlignUp(Math.Max(minLengthAligned, bdpBytes * 2), _requestAlignmentBytes);
            if (bdpFloor > selectedBytes) selectedBytes = bdpFloor;
        }

        int bufferedAheadMs = ConvertBufferedBytesToMs(bufferedAheadBytes);
        bool lowBuffer = bufferedAheadMs < CriticalRefillBufferMs;

        if (!isCritical && !lowBuffer && avgLatencyMs < 800 && demandBytes > 0)
        {
            long demandAligned = AlignUp((long)demandBytes, _requestAlignmentBytes);
            long softCap = demandAligned + _requestAlignmentBytes;
            if (selectedBytes > softCap) selectedBytes = softCap;
        }

        if (isCritical || lowBuffer)
        {
            long criticalFloor = minLengthAligned;
            if (avgLatencyMs > 1200) criticalFloor = Math.Max(criticalFloor, Math.Min(maxLength, minLengthAligned * 2L));
            if (selectedBytes < criticalFloor) selectedBytes = criticalFloor;
        }

        if (selectedBytes > maxLength) selectedBytes = maxLength;
        selectedBytes = AlignUp(selectedBytes, _requestAlignmentBytes);

        long remaining = _contentLength - start;
        if (remaining <= 0) return new DownloadPlan(start, 0);

        if (selectedBytes > remaining) selectedBytes = remaining;
        if (selectedBytes < minimumLength) selectedBytes = Math.Min(AlignUp(minimumLength, _requestAlignmentBytes), remaining);

        int plannedLength = (int)selectedBytes;

        int trimmedLength = TrimLengthToFirstKnownCoverage(start, plannedLength, includeInflight: true);
        if (trimmedLength > 0 && trimmedLength < plannedLength)
            plannedLength = trimmedLength;

        return new DownloadPlan(start, plannedLength);
    }

    private long GetBufferedBytesAhead(long position)
    {
        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        return ramBytes >= diskBytes ? ramBytes : diskBytes;
    }

    private long GetBufferedBytesAheadIncludingInflight(long position)
    {
        long localBuffered = GetBufferedBytesAhead(position);
        long endExclusive = position + localBuffered;
        if (endExclusive >= _contentLength) return _contentLength - position;

        bool extended;
        do
        {
            extended = false;
            lock (_activeDownloadsLock)
            {
                foreach (var active in _activeDownloads.Values)
                {
                    if (active.Start > endExclusive) continue;
                    if (active.EndExclusive <= endExclusive) continue;
                    if (active.EndExclusive <= position) continue;
                    endExclusive = active.EndExclusive;
                    extended = true;
                }
            }
        }
        while (extended && endExclusive < _contentLength);

        return endExclusive - position;
    }

    private bool IsRangeLocallyAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (position < 0 || position >= _contentLength) return false;
        if (_ramCache.ContainsRange(position, length)) return true;
        return _cacheEntry?.IsRangeDownloaded(position, length) == true;
    }

    private int GetAlignedReadLength(long position, int minimumLength)
    {
        if (position >= _contentLength) return 0;
        long remaining = _contentLength - position;
        int aligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        if (aligned > remaining) aligned = (int)remaining;
        return aligned;
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);
    private static int AlignUp(int value, int alignment) { int remainder = value % alignment; return remainder == 0 ? value : value + (alignment - remainder); }
    private static long AlignUp(long value, int alignment) { long remainder = value % alignment; return remainder == 0 ? value : value + (alignment - remainder); }

    #endregion

    #region ReadAtAsync

    /// <summary>
    /// Создаёт фатальное исключение для диапазона, который source не смог получить
    /// после исчерпания всех локальных и сетевых стратегий.
    /// </summary>
    /// <param name="position">Позиция чтения, на которой зафиксирован terminal failure.</param>
    /// <returns>
    /// Экземпляр <see cref="ChunkDownloadFatalException"/>, сигнализирующий верхнему слою,
    /// что повторять чтение этого же диапазона на уровне decoder loop больше нельзя.
    /// </returns>
    private ChunkDownloadFatalException CreateReadAtFatalException(long position)
    {
        long alignedStart = AlignDown(position, _requestAlignmentBytes);
        int logicalIndex = (int)(alignedStart / _requestAlignmentBytes);
        int consecutive403 = Volatile.Read(ref _consecutive403Count);

        Exception inner = _lastDownloadException
            ?? new IOException($"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries");

        return new ChunkDownloadFatalException(
            message: $"Failed to load range at {position} after {ReadAtMaxEpochRetries} retries",
            chunkIndex: logicalIndex,
            consecutiveFailures: consecutive403,
            reason: ChunkDownloadFailureReason.MaxRetriesExceeded,
            trackId: _trackId,
            httpStatusCode: null,
            innerException: inner);
    }

    internal async Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken ct)
    {
        if (position >= _contentLength) return 0;
        int requiredLength = (int)Math.Min(buffer.Length, _contentLength - position);

        if (_ramCache.TryRead(position, buffer, out int ramRead)) return ramRead;

        int diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct)
            .ConfigureAwait(false);
        if (diskRead > 0) return diskRead;

        int epochRetries = 0;
        int consecutiveNetworkFailures = 0;
        bool networkStallPublished = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var downloadToken = CurrentDownloadToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, downloadToken);

                var result = await EnsureRangeAsync(position, requiredLength, linkedCts.Token, isCritical: true)
                    .ConfigureAwait(false);

                switch (result)
                {
                    case RangeDownloadResult.OutOfRange:
                        return 0;

                    case RangeDownloadResult.Success:
                        {
                            if (networkStallPublished)
                            {
                                networkStallPublished = false;
                                consecutiveNetworkFailures = 0;
                                PublishNetworkRecovered();
                            }

                            if (_ramCache.TryRead(position, buffer, out ramRead)) return ramRead;

                            diskRead = await TryLoadRangeFromDiskAsync(position, requiredLength, buffer, ct)
                                .ConfigureAwait(false);
                            if (diskRead > 0) return diskRead;

                            Log.Debug($"[CachingSource] ReadAt {position}: data not at expected " +
                                      "offset after successful download, retrying alignment...");
                            await Task.Delay(ReadAtEpochRetryDelayMs, ct).ConfigureAwait(false);
                            continue;
                        }

                    case RangeDownloadResult.NetworkError:
                    case RangeDownloadResult.SlotTimeout:
                        {
                            consecutiveNetworkFailures++;
                            CheckAndPublishNetworkStall(
                                ref networkStallPublished, consecutiveNetworkFailures, position);

                            int backoffMs = ComputeNetworkRetryBackoff(consecutiveNetworkFailures);
                            await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                            continue;
                        }

                    case RangeDownloadResult.Cancelled:
                        ct.ThrowIfCancellationRequested();
                        if (downloadToken.IsCancellationRequested)
                        {
                            // Epoch сменился (seek, URL refresh, rebuild) — конечный retry.
                            epochRetries++;
                            if (epochRetries >= ReadAtMaxEpochRetries)
                                throw CreateReadAtFatalException(position);
                            await Task.Delay(ReadAtEpochRetryDelayMs, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // Отмена без смены epoch — ObjectDisposed от Rebuild.
                            // Трактуем как transient сетевую ошибку.
                            consecutiveNetworkFailures++;
                            CheckAndPublishNetworkStall(
                                ref networkStallPublished, consecutiveNetworkFailures, position);
                            await Task.Delay(
                                ComputeNetworkRetryBackoff(consecutiveNetworkFailures), ct)
                                .ConfigureAwait(false);
                        }
                        continue;

                    default:
                        consecutiveNetworkFailures++;
                        await Task.Delay(ComputeNetworkRetryBackoff(consecutiveNetworkFailures), ct)
                            .ConfigureAwait(false);
                        continue;
                }
            }
            catch (ChunkDownloadFatalException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                epochRetries++;
                if (epochRetries >= ReadAtMaxEpochRetries)
                {
                    Log.Warn($"[CachingSource] ReadAt {position}: epoch retries exhausted " +
                             $"({ReadAtMaxEpochRetries})");
                    throw CreateReadAtFatalException(position);
                }

                Log.Debug($"[CachingSource] ReadAt at {position}: epoch changed, " +
                          $"retry {epochRetries}/{ReadAtMaxEpochRetries}");
                await Task.Delay(ReadAtEpochRetryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> TryLoadRangeFromDiskAsync(long position, int minimumLength, Memory<byte> target, CancellationToken ct)
    {
        if (_cacheEntry == null) return 0;
        if (!_cacheEntry.TryGetContainingRange(position, out long rangeStart, out long rangeEndExclusive)) return 0;

        long loadStart = AlignDown(position, _requestAlignmentBytes);
        if (loadStart < rangeStart) loadStart = rangeStart;

        int desiredLength = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        long available = rangeEndExclusive - loadStart;
        if (available <= 0) return 0;
        if (desiredLength > available) desiredLength = (int)available;

        var diskResult = await _cacheManager.ReadRangeAsync(_cacheKey, loadStart, desiredLength, ct).ConfigureAwait(false);
        if (!diskResult.HasValue) return 0;

        var (owner, length) = diskResult.Value;
        var block = new RamRangeBlock(loadStart, owner, length);
        int copied = CopyFromBlock(block.Memory.Span, (int)(position - loadStart), target);

        if (!_ramCache.TryAdd(block)) block.Dispose();
        return copied;
    }

    private static int CopyFromBlock(ReadOnlySpan<byte> blockData, int offset, Memory<byte> buffer)
    {
        int available = Math.Min(buffer.Length, blockData.Length - offset);
        if (available <= 0) return 0;
        blockData.Slice(offset, available).CopyTo(buffer.Span);
        return available;
    }

    #endregion

    #region Active Download Coordination

    private int GetActiveDownloadCount() { lock (_activeDownloadsLock) return _activeDownloads.Count; }

    /// <summary>
    /// Строгая защита от пересекающихся (overlapping) загрузок. 
    /// Это критически важно на слабой сети, чтобы не качать одни и те же байты дважды.
    /// </summary>
    private bool TryGetOverlappingActiveDownload(long start, int length, out ActiveRangeDownload? active)
    {
        long endExclusive = start + length;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (start < current.EndExclusive && endExclusive > current.Start)
                {
                    active = current;
                    return true;
                }
            }
        }
        active = null; return false;
    }

    private ActiveRangeDownload RegisterOrGetActiveDownload(ActiveRangeDownload candidate)
    {
        long candidateEnd = candidate.EndExclusive;
        lock (_activeDownloadsLock)
        {
            foreach (var current in _activeDownloads.Values)
            {
                if (candidate.Start < current.EndExclusive && candidateEnd > current.Start)
                    return current;
            }

            if (_activeDownloads.TryGetValue(candidate.Start, out var sameStart))
                return sameStart;

            _activeDownloads.Add(candidate.Start, candidate);
            return candidate;
        }
    }

    private void RemoveActiveDownloadIfOwner(long key, ActiveRangeDownload owner)
    {
        lock (_activeDownloadsLock)
        {
            if (_activeDownloads.TryGetValue(key, out var current) && ReferenceEquals(current, owner))
                _activeDownloads.Remove(key);
        }
    }

    private static async Task WaitForActiveDownloadAsync(Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (ChunkDownloadFatalException) { throw; }
        catch { }
    }

    #endregion

    #region EnsureRangeAsync

    /// <summary>
    /// Гарантирует наличие диапазона данных, предотвращая бесконечные ретраи (Retry Storm).
    /// </summary>
    private async Task<RangeDownloadResult> EnsureRangeAsync(
        long position,
        int minimumLength,
        CancellationToken ct,
        bool isCritical = false)
    {
        if (position < 0 || position >= _contentLength)
            return RangeDownloadResult.OutOfRange;

        if (minimumLength <= 0)
            return RangeDownloadResult.Success;

        minimumLength = (int)Math.Min(minimumLength, _contentLength - position);

        if (IsRangeLocallyAvailable(position, minimumLength))
            return RangeDownloadResult.Success;

        ct.ThrowIfCancellationRequested();

        int maxAttempts = _config.MaxNetworkRetries;
        int chunkIoExceptions = 0;

        // Флаг: опубликовали ли мы уже предупреждение через PublishSourceWarning.
        // Нужен чтобы не спамить одним и тем же сообщением при каждом retry.
        bool warningPublished = false;

        // Счётчик последовательных refresh, которые не дали нового URL.
        // Если набрать слишком много — смысла ретраить нет.
        int consecutiveStaleRefreshes = 0;
        const int MaxStaleRefreshesBeforeGivingUp = 3;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (IsRangeLocallyAvailable(position, minimumLength))
                return RangeDownloadResult.Success;

            if (TryGetOverlappingActiveDownload(position, minimumLength, out var overlapping))
            {
                await WaitForActiveDownloadAsync(overlapping!.LazyTask.Value, ct).ConfigureAwait(false);
                continue;
            }

            var plan = BuildDownloadPlan(position, minimumLength, isCritical);
            var ownerLazy = new Lazy<Task<RangeDownloadResult>>(
                () => DownloadRangeCoreAsync(plan, ct, isCritical),
                LazyThreadSafetyMode.ExecutionAndPublication);

            var candidate = new ActiveRangeDownload(plan.Start, plan.Length, ownerLazy);
            var actual = RegisterOrGetActiveDownload(candidate);

            if (!ReferenceEquals(actual, candidate))
            {
                // Другой caller уже регистрирует этот же диапазон — ждём его.
                await WaitForActiveDownloadAsync(actual.LazyTask.Value, ct).ConfigureAwait(false);
                continue;
            }

            // Мы владелец download task — запускаем и обрабатываем результат
            RangeDownloadResult result;
            try
            {
                result = await actual.LazyTask.Value.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Epoch сменился (например, seek отменил старые загрузки) — это нормально,
                // просто повторяем попытку с новым download token.
                continue;
            }
            finally
            {
                RemoveActiveDownloadIfOwner(plan.Start, actual);
            }

            switch (result)
            {
                case RangeDownloadResult.Success:
                    return RangeDownloadResult.Success;

                // 403 Forbidden
                case RangeDownloadResult.Forbidden403:
                    {
                        // Сообщаем наружу (через PlaybackErrorOrchestrator) о проблеме
                        // ровно один раз — чтобы UI мог показать индикатор или запустить recovery.
                        if (isCritical && !warningPublished)
                        {
                            warningPublished = true;
                            PublishSourceWarning(
                                new UnauthorizedAccessException(
                                    $"Critical range {plan.Start}-{plan.Start + plan.Length - 1L} " +
                                    $"received HTTP 403 and requires stream URL refresh"));
                        }

                        // При 403 отменяем ВСЕ параллельные preload-запросы:
                        // они используют тот же мёртвый URL и тоже получат 403.
                        // Гарантирует что после получения нового URL не будет race
                        // между старыми in-flight запросами и новыми.
                        ResetDownloadEpoch();

                        Log.Warn($"[CachingSource] [{_trackId}] HTTP 403 on range {plan.Start}-{plan.Start + plan.Length - 1L}. " +
                                $"Attempt {attempt + 1}/{maxAttempts}. Refreshing URL...");

                        UrlRefreshOutcome outcome;
                        try
                        {
                            outcome = await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
                        }
                        catch (ChunkDownloadFatalException)
                        {
                            // Circuit breaker открылся — пробрасываем fatal наверх.
                            throw;
                        }

                        switch (outcome)
                        {
                            case UrlRefreshOutcome.Success:
                                // URL реально обновился — продолжаем retry.
                                consecutiveStaleRefreshes = 0;
                                Log.Info($"[CachingSource] [{_trackId}] URL refreshed, retrying range {plan.Start}");
                                continue;

                            case UrlRefreshOutcome.StaleToken:
                                // URL изменился, но n-token тот же — скорее всего session cache
                                // вернул старый manifest. Следующий retry скорее всего снова 403.
                                // Даём небольшую задержку перед следующей попыткой.
                                consecutiveStaleRefreshes++;
                                Log.Warn($"[CachingSource] [{_trackId}] Stale n-token after refresh " +
                                        $"({consecutiveStaleRefreshes}/{MaxStaleRefreshesBeforeGivingUp}). " +
                                        $"Backing off before retry...");

                                if (consecutiveStaleRefreshes >= MaxStaleRefreshesBeforeGivingUp)
                                {
                                    // Слишком много refresh с одинаковым token — сдаёмся.
                                    Log.Error($"[CachingSource] [{_trackId}] Too many stale refreshes. " +
                                            $"Escalating to fatal.");
                                    throw CreateReadAtFatalException(position);
                                }

                                // Exponential backoff: 1s, 2s, 4s
                                int backoffMs = (int)Math.Pow(2, consecutiveStaleRefreshes - 1) * 1000;
                                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                                continue;

                            case UrlRefreshOutcome.NoChange:
                                // Refresh не вернул URL — API недоступен или timeout.
                                // Это transient: не инкрементим consecutiveStaleRefreshes,
                                // не открываем circuit breaker. Просто backoff и retry.
                                Log.Warn($"[CachingSource] [{_trackId}] Refresh returned no new URL. " +
                                        $"Backing off before retry ({attempt + 1}/{maxAttempts})...");
                                int noUrlBackoffMs = Math.Min((attempt + 1) * 2000, 8000);
                                await Task.Delay(noUrlBackoffMs, ct).ConfigureAwait(false);
                                continue;

                            default:
                                continue;
                        }
                    }

                case RangeDownloadResult.NetworkError:
                    {
                        chunkIoExceptions++;

                        if (isCritical && !warningPublished && chunkIoExceptions >= 2)
                        {
                            warningPublished = true;
                            PublishSourceWarning(
                                new IOException(
                                    $"Critical range {plan.Start}-{plan.Start + plan.Length - 1L} " +
                                    $"failed repeatedly and playback may stall"));
                        }

                        // Quadratic backoff: 100ms, 400ms, 900ms, 1600ms, 2000ms (cap)
                        int delay = (int)Math.Min(2000, 100 * Math.Pow(attempt + 1, 2));
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue;
                    }

                case RangeDownloadResult.Cancelled:
                    ct.ThrowIfCancellationRequested();
                    return result;

                default:
                    return result;
            }
        }

        // Исчерпали все попытки — сигнализируем fatal.
        return RangeDownloadResult.NetworkError;
    }

    /// <summary>
    /// Гарантирует наличие валидного continuation URL перед сетевой загрузкой.
    /// Реализует single-flight модель: если URL уже есть — возвращает сразу,
    /// если нет — либо ждёт внешнего attach, либо запускает самостоятельный acquire.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c> если URL доступен; <c>false</c> если получить не удалось.</returns>
    private async Task<bool> EnsureUrlAvailableAsync(CancellationToken ct)
    {
        // URL уже есть — ничего делать не нужно.
        if (!string.IsNullOrWhiteSpace(_currentUrl))
            return true;

        Log.Debug($"[CachingSource] [{_trackId}] EnsureUrlAvailable: no URL yet, " +
                $"hasAcquirer={_urlAcquirer != null}");

        Task<string?> waitTask;
        bool isInitiator = false;

        lock (_continuationLock)
        {
            // Перепроверяем под локом — мог прийти через TryAttachContinuationUrl.
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                return true;

            if (_continuationUrlTcs != null)
            {
                // Кто-то уже запустил resolution — просто ждём его результата.
                waitTask = _continuationUrlTcs.Task;
            }
            else
            {
                // Мы первые — создаём promise и становимся initiator.
                var tcs = new TaskCompletionSource<string?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _continuationUrlTcs = tcs;
                waitTask = tcs.Task;
                isInitiator = true;
            }
        }

        // Initiator запускает фактическое получение URL в фоне.
        if (isInitiator && _urlAcquirer != null)
            _ = ResolveContinuationUrlSingleFlightAsync(ct);

        try
        {
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Если URL успел прийти до отмены — считаем успехом.
            return !string.IsNullOrWhiteSpace(_currentUrl);
        }

        Log.Info($"[CachingSource] [{_trackId}] EnsureUrlAvailable resolved: " +
                $"hasUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

        // Защита от retry-шторма: если URL так и не получен — делаем паузу,
        // чтобы preload loop не уходил в бесконечный tight-retry.
        if (string.IsNullOrWhiteSpace(_currentUrl))
        {
            Log.Warn($"[CachingSource] [{_trackId}] URL acquirer returned null. " +
                    $"Delaying to prevent retry storm...");
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { }
        }

        return !string.IsNullOrWhiteSpace(_currentUrl);
    }

    /// <summary>
    /// Single-flight resolution continuation URL через <see cref="_urlAcquirer"/>.
    /// Результат доставляется через <see cref="_continuationUrlTcs"/>.
    /// </summary>
    private async Task ResolveContinuationUrlSingleFlightAsync(CancellationToken ct)
    {
        string? resolvedUrl = null;

        try
        {
            Log.Debug($"[CachingSource] [{_trackId}] Acquiring continuation URL (single-flight)...");
            resolvedUrl = await _urlAcquirer!(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] [{_trackId}] Continuation URL resolution failed: {ex.Message}");
        }

        TaskCompletionSource<string?>? tcs;

        lock (_continuationLock)
        {
            tcs = _continuationUrlTcs;
            _continuationUrlTcs = null;

            // Принимаем URL только если source ещё не получил его из другого источника.
            if (!string.IsNullOrEmpty(resolvedUrl) && string.IsNullOrWhiteSpace(_currentUrl))
            {
                _currentUrl = resolvedUrl;

                if (_cacheEntry != null)
                    _cacheEntry.OriginalUrl = resolvedUrl;

                Log.Info($"[CachingSource] [{_trackId}] Continuation URL resolved via single-flight");
            }
        }

        tcs?.TrySetResult(resolvedUrl);
    }

    #endregion

    #region DownloadRangeCoreAsync

    private async Task<RangeDownloadResult> DownloadRangeCoreAsync(DownloadPlan plan, CancellationToken ct, bool isCritical)
    {
        if (_disposed) return RangeDownloadResult.Cancelled;
        if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;
        bool gotSlot = false;

        try
        {
            if (!isCritical)
            {
                gotSlot = await _downloadSlots.WaitAsync(_config.DownloadSlotTimeoutMs, ct).ConfigureAwait(false);
                if (!gotSlot) return RangeDownloadResult.SlotTimeout;
            }

            if (_disposed) return RangeDownloadResult.Cancelled;
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;
            if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;

            if (string.IsNullOrWhiteSpace(_currentUrl))
            {
                bool urlReady = await EnsureUrlAvailableAsync(ct).ConfigureAwait(false);
                if (!urlReady)
                {
                    Log.Warn($"[CachingSource] Range {plan.Start}: continuation URL is unavailable");
                    return ct.IsCancellationRequested ? RangeDownloadResult.Cancelled : RangeDownloadResult.NetworkError;
                }
            }

            return await DownloadRangeHttpAsync(plan, ct).ConfigureAwait(false);
        }
        catch (ChunkDownloadFatalException) { throw; }
        // Source уничтожен или epoch отменён — настоящая отмена.
        catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)
        { return RangeDownloadResult.Cancelled; }
        // HttpClient пересобран (Rebuild) — transient, не отмена.
        catch (ObjectDisposedException)
        { return RangeDownloadResult.NetworkError; }
        catch (OperationCanceledException) { return RangeDownloadResult.Cancelled; }
        catch (Exception) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Range {plan.Start} unexpected: {ex.Message}");
            return RangeDownloadResult.NetworkError;
        }
        finally
        {
            if (gotSlot) { try { _downloadSlots.Release(); } catch (ObjectDisposedException) { } }
        }
    }

    /// <summary>
    /// Вычисляет длину префикса скачанного блока, который можно безопасно закоммитить в RAM без overlap.
    /// </summary>
    /// <param name="start">Начало скачанного диапазона.</param>
    /// <param name="actualLength">Фактически скачанная длина.</param>
    /// <returns>
    /// Длина начального non-overlapping gap.
    /// Может быть меньше <paramref name="actualLength"/>, если хвост диапазона уже есть локально.
    /// </returns>
    private int ComputeRamCommitLength(long start, int actualLength)
    {
        if (actualLength <= 0) return 0;
        return TrimLengthToFirstKnownCoverage(start, actualLength, includeInflight: false);
    }

    private async Task<RangeDownloadResult> DownloadRangeHttpAsync(DownloadPlan plan, CancellationToken ct)
    {
        int rn = Interlocked.Increment(ref _requestSequenceNumber);
        long end = plan.Start + plan.Length - 1;
        int logicalIndex = (int)(plan.Start / _requestAlignmentBytes);

        Log.Debug($"[CachingSource] Range {plan.Start}-{end}: GET rn={rn}");

        using var request = CreateRangeRequest(logicalIndex, plan.Start, end, rn);
        HttpResponseMessage response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            response = await CurrentHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode) SaveLatency(sw.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested || _disposed) { return RangeDownloadResult.Cancelled; }
        catch (HttpRequestException ex) when (IsCancelledSendFailure(ex, ct, _disposed)) { return RangeDownloadResult.Cancelled; }
        catch (TaskCanceledException) { return RangeDownloadResult.NetworkError; }
        catch (HttpRequestException) { return RangeDownloadResult.NetworkError; }

        using (response)
        {
            if (ct.IsCancellationRequested) return RangeDownloadResult.Cancelled;

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Interlocked.Increment(ref _consecutive403Count);
                await LogAndDiagnose403Async(logicalIndex, request, response).ConfigureAwait(false);
                return RangeDownloadResult.Forbidden403;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Log.Debug($"[CachingSource] Range {plan.Start}-{end}: 416 (EOF)");
                return RangeDownloadResult.OutOfRange;
            }

            Volatile.Write(ref _consecutive403Count, 0);

            // Регистрация CDN-хоста для спекулятивного прогрева
            // После первого успешного ответа запоминаем CDN-ноду.
            // При следующем PlayTrack AudioEngine.PreWarmCdnConnections откроет
            // соединение к этому хосту ДО YouTube API call, перекрывая TLS-рукопожатие.
            Http.CdnConnectionPreWarmer.RecordHost(_currentUrl);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
                throw new ChunkDownloadFatalException("YouTube returned encrypted UMP format", chunkIndex: logicalIndex, consecutiveFailures: 0, reason: ChunkDownloadFailureReason.UmpFormat, trackId: _trackId);

            response.EnsureSuccessStatusCode();

            try
            {
                using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(plan.Length);
                int actualLength;
                var bodySw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    actualLength = await ReadStreamFullyAsync(contentStream, memoryOwner.Memory[..plan.Length], ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    memoryOwner.Dispose();
                    throw new OperationCanceledException("HTTP stream disposed during read (seek/stop)");
                }
                catch (OperationCanceledException)
                {
                    memoryOwner.Dispose();
                    throw;
                }
                catch (Exception ex) when (ct.IsCancellationRequested || _disposed || ex is IOException || ex is System.Net.Sockets.SocketException)
                {
                    _lastDownloadException = ex;
                    memoryOwner.Dispose();

                    if (ct.IsCancellationRequested || _disposed) return RangeDownloadResult.Cancelled;

                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read I/O error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                catch (Exception ex)
                {
                    _lastDownloadException = ex;
                    memoryOwner.Dispose();

                    Log.Warn($"[CachingSource] Range {plan.Start}-{end} read error: {ex.Message}");
                    return RangeDownloadResult.NetworkError;
                }
                finally
                {
                    bodySw.Stop();
                }

                // Увеличены пороги: игнорируем микро-замеры (< 16KB или < 20мс), 
                // так как они дают ложные всплески из-за TCP Slow Start или кэшей ОС.
                if (actualLength >= 16384 && bodySw.Elapsed.TotalMilliseconds >= 20)
                {
                    double speedBytesPerSec = actualLength / (bodySw.Elapsed.TotalMilliseconds / 1000.0);
                    SaveBandwidth(speedBytesPerSec, actualLength);
                }

                if (ct.IsCancellationRequested)
                {
                    memoryOwner.Dispose();
                    return RangeDownloadResult.Cancelled;
                }

                if (actualLength == 0)
                {
                    memoryOwner.Dispose();
                    Log.Warn($"[CachingSource] Range {plan.Start}-{end}: empty response");
                    return RangeDownloadResult.NetworkError;
                }

                if (actualLength < plan.Length)
                {
                    bool isNearEof = plan.Start + actualLength >= _contentLength;
                    if (!isNearEof)
                    {
                        memoryOwner.Dispose();
                        Log.Warn($"[CachingSource] Range {plan.Start}-{end} incomplete: {actualLength}/{plan.Length}");
                        return RangeDownloadResult.NetworkError;
                    }
                }

                // disk copy создаётся всегда, независимо от того, удастся ли закоммитить блок в RAM.
                // Иначе overlap-case превращается в "скачал и выбросил", после чего тот же range
                // будет запрошен повторно бесконечно.
                byte[] diskCopy = ArrayPool<byte>.Shared.Rent(actualLength);
                memoryOwner.Memory.Span[..actualLength].CopyTo(diskCopy.AsSpan(0, actualLength));

                int ramCommitLength = ComputeRamCommitLength(plan.Start, actualLength);
                bool committedToRam = false;

                if (ramCommitLength > 0)
                {
                    var block = new RamRangeBlock(plan.Start, memoryOwner, ramCommitLength);
                    if (_ramCache.TryAdd(block))
                    {
                        committedToRam = true;
                    }
                    else
                    {
                        block.Dispose();
                    }
                }
                else
                {
                    memoryOwner.Dispose();
                }

                _ = WriteToDiskTrackedAsync(plan.Start, diskCopy, actualLength);

                if (!committedToRam)
                {
                    Log.Debug($"[CachingSource] Range {plan.Start}-{end}: RAM overlap avoided, " +
                              $"committed_prefix={ramCommitLength}/{actualLength}, disk_write=scheduled");
                }

                if (_ramCache.TotalBytes > _config.MaxRamBytes)
                    _ramCache.Trim(Volatile.Read(ref _currentReadOffset), _config.RamEvictionWindowBytes, _config.MaxRamBytes);

                return RangeDownloadResult.Success;
            }
            catch (OperationCanceledException)
            {
                return RangeDownloadResult.Cancelled;
            }
        }
    }

    private static async ValueTask<int> ReadStreamFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Записывает скачанные данные на диск с отслеживанием pending-count
    /// для безопасного освобождения lease при dispose source.
    /// </summary>
    private async Task WriteToDiskTrackedAsync(long offset, byte[] rentedCopy, int length)
    {
        Interlocked.Increment(ref _pendingDiskWrites);
        try
        {
            await _cacheManager.WriteRangeAsync(
                _cacheKey, offset, rentedCopy.AsMemory(0, length), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Warn($"[CachingSource] Disk write range {offset}-{offset + length - 1}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedCopy);
            Interlocked.Decrement(ref _pendingDiskWrites);
        }
    }

    #endregion

    #region 403 Diagnostics

    private async Task LogAndDiagnose403Async(int logicalIndex, HttpRequestMessage request, HttpResponseMessage response)
    {
        int count = Volatile.Read(ref _consecutive403Count);
        var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
        var reqUa = request.Headers.UserAgent.ToString();

        Log.Warn($"[CachingSource] 403 DIAGNOSTIC range@{logicalIndex} (consecutive={count})");
        Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
        Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} (len={nParam?.Length ?? 0}, looks_encrypted={nParam?.Length is > 15 and < 25})");

        if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
            Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

        string responseBody = "";
        try { responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        if (responseBody.Length > 0) Log.Warn($"[CachingSource]   Body: {responseBody[..Math.Min(responseBody.Length, 200)]}");
        Log.Warn("[CachingSource]══");
    }

    #endregion

    #region URL Refresh

    /// <summary>
    /// Результат попытки обновления stream URL.
    /// </summary>
    private enum UrlRefreshOutcome
    {
        /// <summary>URL успешно обновлён, новый n-token отличается от старого.</summary>
        Success,

        /// <summary>
        /// URL получен, но n-token не изменился — вероятно, кэш вернул протухший URL.
        /// Retry с этим URL приведёт к повторному 403.
        /// </summary>
        StaleToken,

        /// <summary>
        /// URL не изменился вообще — refresher вернул null или тот же URL.
        /// </summary>
        NoChange,

        /// <summary>
        /// Circuit breaker открыт: слишком много последовательных неудачных refresh.
        /// Дальнейшие попытки бессмысленны.
        /// </summary>
        CircuitOpen
    }

    /// <summary>
    /// Координирует обновление stream URL при получении HTTP 403.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Исход попытки обновления URL.</returns>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается когда circuit breaker открыт (слишком много failures подряд).
    /// </exception>
    private async Task<UrlRefreshOutcome> CoordinatedRefreshAsync(CancellationToken ct)
    {
        // Circuit Breaker
        // Если refresh уже падал MaxRefreshFailuresBeforeCircuitBreak раз подряд —
        // дальнейшие попытки только провоцируют bot detection на стороне YouTube.
        int refreshFailures = Volatile.Read(ref _consecutiveRefreshFailures);
        if (refreshFailures >= MaxRefreshFailuresBeforeCircuitBreak)
        {
            throw new ChunkDownloadFatalException(
                message: $"URL refresh circuit breaker open after {refreshFailures} consecutive failures",
                chunkIndex: -1,
                consecutiveFailures: Volatile.Read(ref _consecutive403Count),
                reason: ChunkDownloadFailureReason.Forbidden403,
                trackId: _trackId,
                httpStatusCode: 403);
        }

        if (_disposed)
            return UrlRefreshOutcome.NoChange;

        // Single-Flight: пробуем взять lock немедленно (без ожидания)
        bool isInitiator;
        try
        {
            isInitiator = await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return UrlRefreshOutcome.NoChange;
        }

        // Waiter path: кто-то уже делает refresh — ждём его завершения
        if (!isInitiator)
        {
            return await WaitForConcurrentRefreshAsync(ct).ConfigureAwait(false);
        }

        // Initiator path: мы выиграли lock — делаем фактический refresh
        try
        {
            return await ExecuteRefreshAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Всегда освобождаем lock — иначе все waiters зависнут навсегда.
            try { _refreshLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Ожидает завершения concurrent refresh, инициированного другим caller'ом,
    /// и возвращает его результат.
    /// </summary>
    private async Task<UrlRefreshOutcome> WaitForConcurrentRefreshAsync(CancellationToken ct)
    {
        Log.Debug($"[CachingSource] [{_trackId}] Waiting for concurrent URL refresh...");

        try
        {
            // Ждём пока initiator закончит refresh и отпустит lock.
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            _refreshLock.Release();
        }
        catch (ObjectDisposedException)
        {
            return UrlRefreshOutcome.NoChange;
        }

        // Если после завершения concurrent refresh circuit breaker открылся —
        // сигнализируем об этом, чтобы не делать ещё один безнадёжный retry.
        if (Volatile.Read(ref _consecutiveRefreshFailures) >= MaxRefreshFailuresBeforeCircuitBreak)
        {
            throw new ChunkDownloadFatalException(
                message: "URL refresh circuit breaker open after waiting for concurrent refresh",
                chunkIndex: -1,
                consecutiveFailures: Volatile.Read(ref _consecutive403Count),
                reason: ChunkDownloadFailureReason.Forbidden403,
                trackId: _trackId,
                httpStatusCode: 403);
        }

        // Небольшой delay после чужого refresh — даём CDN время "переварить" новый URL
        // прежде чем мы начнём слать range-запросы.
        await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);

        // Проверяем реальный результат: сбросился ли счётчик 403?
        // Если да — concurrent refresh был успешным, URL сменился.
        bool concurrentRefreshSucceeded = Volatile.Read(ref _consecutive403Count) == 0;

        Log.Debug($"[CachingSource] [{_trackId}] Concurrent refresh result: " +
                $"{(concurrentRefreshSucceeded ? "success" : "failed")}");

        return concurrentRefreshSucceeded
            ? UrlRefreshOutcome.Success
            : UrlRefreshOutcome.NoChange;
    }

    /// <summary>
    /// Выполняет фактическое обновление URL (только initiator path).
    /// </summary>
    private async Task<UrlRefreshOutcome> ExecuteRefreshAsync(CancellationToken ct)
    {
        // Cooldown
        // Защита от refresh-шторма: если последний refresh был совсем недавно —
        // ждём cooldown. Это особенно важно когда несколько chunk-загрузок
        // параллельно получают 403 и каждая пытается запустить свой refresh.
        var elapsed = DateTime.UtcNow - _lastRefreshTime;
        if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
        {
            int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
            Log.Debug($"[CachingSource] [{_trackId}] Refresh cooldown: waiting {waitMs}ms");
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }

        // Запоминаем текущий URL ДО refresh — нужен для сравнения после.
        string? urlBeforeRefresh = _currentUrl;
        string? nTokenBefore = UrlEx.TryGetQueryParameterValue(urlBeforeRefresh, "n");

        Log.Info($"[CachingSource] [{_trackId}] Executing URL refresh. " +
                $"Current n-token: {nTokenBefore?[..Math.Min(nTokenBefore.Length, 10)] ?? "MISSING"}...");

        await RefreshUrlAsync(ct).ConfigureAwait(false);
        _lastRefreshTime = DateTime.UtcNow;

        string? urlAfterRefresh = _currentUrl;
        string? nTokenAfter = UrlEx.TryGetQueryParameterValue(urlAfterRefresh, "n");

        // Проверка результата refresh

        // Случай 1: refresher вернул null или URL не изменился вообще.
        // Это происходит когда: API недоступен, session expired, или refresher не настроен.
        bool urlActuallyChanged = !string.Equals(urlBeforeRefresh, urlAfterRefresh, StringComparison.Ordinal)
                                && !string.IsNullOrWhiteSpace(urlAfterRefresh);

        if (!urlActuallyChanged)
        {
            // НЕ инкрементим _consecutiveRefreshFailures.
            // NoChange = refresh не вернул URL (timeout, network error, API unavailable).
            // Это transient — не повод для circuit break.
            // Circuit breaker считает только StaleToken (URL получен, но n-token тот же).
            Log.Warn($"[CachingSource] [{_trackId}] Refresh returned no new URL " +
                    $"(transient, circuit breaker not incremented)");
            return UrlRefreshOutcome.NoChange;
        }

        // Случай 2: URL изменился, но n-token остался прежним.
        // Это значит YouTube вернул тот же подписанный URL через session/memory cache.
        // Retry с ним снова приведёт к 403.
        bool nTokenChanged = !string.IsNullOrEmpty(nTokenAfter)
                            && !string.Equals(nTokenBefore, nTokenAfter, StringComparison.Ordinal);

        if (!nTokenChanged && !string.IsNullOrEmpty(nTokenBefore))
        {
            int failures = Interlocked.Increment(ref _consecutiveRefreshFailures);
            Log.Warn($"[CachingSource] [{_trackId}] Refresh got new URL but n-token unchanged " +
                    $"— likely stale session cache (failure {failures}/{MaxRefreshFailuresBeforeCircuitBreak})");

            await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
            return UrlRefreshOutcome.StaleToken;
        }

        // Случай 3: Успех — URL изменился И n-token обновился.
        // Сбрасываем счётчики ТОЛЬКО здесь, когда refresh реально помог.
        Volatile.Write(ref _consecutive403Count, 0);
        Volatile.Write(ref _consecutiveRefreshFailures, 0);

        Log.Info($"[CachingSource] [{_trackId}] URL refresh successful. " +
                $"New n-token: {nTokenAfter?[..Math.Min(nTokenAfter.Length, 10)] ?? "?"}...");

        await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
        return UrlRefreshOutcome.Success;
    }

    #endregion

    #region HTTP Request Building

    private HttpRequestMessage CreateRangeRequest(int logicalIndex, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains("googlevideo.com/videoplayback", StringComparison.Ordinal);

        if (isYouTube)
        {
            string url = BuildYouTubeRangeUrl(_currentUrl, rn);
            LogRangeRequestParams(logicalIndex, url);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            SharedHttpClient.ApplyUserAgentFromUrl(request, url);
            string ua = request.Headers.UserAgent.ToString();
            Log.Debug($"[CachingSource] Range {logicalIndex} UA: {ua[..Math.Min(60, ua.Length)]}...");
            return request;
        }

        var genericRequest = new HttpRequestMessage(HttpMethod.Get, _currentUrl);
        genericRequest.Headers.Range = new RangeHeaderValue(start, end);
        genericRequest.Headers.TryAddWithoutValidation("User-Agent", YoutubeClientUtils.UaWebRemix);
        return genericRequest;
    }

    private static void LogRangeRequestParams(int logicalIndex, string url)
    {
        var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");
        Log.Debug($"[CachingSource] Range {logicalIndex} URL: {url[..Math.Min(url.Length, 300)]}");
        Log.Debug($"[CachingSource] Range {logicalIndex} params: c={cParam ?? "MISSING"}, n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., sig={(sigParam is not null ? $"{sigParam.Length}chars" : "MISSING")}");
    }

    private static string BuildYouTubeRangeUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Capped exponential backoff для transient network retry.
    /// Прогрессия: 500мс → 1с → 2с → 4с → 5с (потолок).
    /// </summary>
    private static int ComputeNetworkRetryBackoff(int consecutiveFailures)
    {
        int exponent = Math.Min(consecutiveFailures - 1, 3);
        int backoffMs = NetworkRetryBaseMs * (1 << Math.Max(0, exponent));
        return Math.Min(backoffMs, NetworkRetryMaxBackoffMs);
    }

    /// <summary>
    /// Однократная публикация network stall event при достижении порога.
    /// </summary>
    private void CheckAndPublishNetworkStall(
        ref bool published, int consecutiveFailures, long position)
    {
        if (published || consecutiveFailures < NetworkStallThreshold) return;

        published = true;
        Log.Warn($"[CachingSource] [{_trackId}] Network stall at position {position} " +
                 $"after {consecutiveFailures} consecutive failures. " +
                 "Waiting for recovery (infinite retry)...");

        try { OnNetworkStalled?.Invoke(_trackId); }
        catch (Exception ex) { Log.Warn($"[CachingSource] NetworkStalled handler: {ex.Message}"); }
    }

    /// <summary>
    /// Публикация восстановления сети после stall.
    /// </summary>
    private void PublishNetworkRecovered()
    {
        Log.Info($"[CachingSource] [{_trackId}] Network recovered — data flow restored");
        try { OnNetworkRecovered?.Invoke(_trackId); }
        catch (Exception ex) { Log.Warn($"[CachingSource] NetworkRecovered handler: {ex.Message}"); }
    }

    /// <summary>
    /// Проверяет, покрыт ли диапазон полностью уже локально либо активной in-flight загрузкой.
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    /// <returns><c>true</c>, если диапазон уже доступен локально или гарантированно качается.</returns>
    private bool IsRangeKnownAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (IsRangeLocallyAvailable(position, length)) return true;
        return IsRangeCoveredByInflight(position, length);
    }

    /// <summary>
    /// Проверяет, покрыт ли диапазон полностью уже зарегистрированной активной загрузкой.
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    /// <returns><c>true</c>, если диапазон полностью лежит внутри in-flight range.</returns>
    private bool IsRangeCoveredByInflight(long position, int length)
    {
        long endExclusive = position + length;

        lock (_activeDownloadsLock)
        {
            foreach (var active in _activeDownloads.Values)
            {
                if (position < active.Start) continue;
                if (endExclusive > active.EndExclusive) continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Обрезает планируемую длину загрузки до первого уже известного aligned coverage впереди.
    /// </summary>
    /// <param name="start">Начало диапазона.</param>
    /// <param name="length">Исходная длина диапазона.</param>
    /// <param name="includeInflight">
    /// <c>true</c> — учитывать in-flight загрузки как покрытие;
    /// <c>false</c> — учитывать только локально доступные данные.
    /// </param>
    /// <returns>
    /// Длину первого непрерывного "gap" от <paramref name="start"/> до ближайшего уже доступного aligned диапазона.
    /// Если покрытия впереди нет — возвращает исходную <paramref name="length"/>.
    /// </returns>
    private int TrimLengthToFirstKnownCoverage(long start, int length, bool includeInflight)
    {
        if (length <= _requestAlignmentBytes) return length;

        long endExclusive = start + length;
        long probe = start + _requestAlignmentBytes;

        while (probe < endExclusive)
        {
            int probeLength = (int)Math.Min(_requestAlignmentBytes, endExclusive - probe);
            bool covered = includeInflight
                ? IsRangeKnownAvailable(probe, probeLength)
                : IsRangeLocallyAvailable(probe, probeLength);

            if (covered)
                return (int)(probe - start);

            probe += _requestAlignmentBytes;
        }

        return length;
    }

    private static bool IsCancelledSendFailure(HttpRequestException exception, CancellationToken ct, bool disposed)
    {
        if (disposed || ct.IsCancellationRequested) return true;
        Exception? current = exception;
        while (current != null)
        {
            switch (current)
            {
                case ObjectDisposedException: return true;
                case System.Net.Sockets.SocketException socketException when socketException.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted or System.Net.Sockets.SocketError.Interrupted: return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    #endregion
}