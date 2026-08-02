namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- Section: DownloadPlan ---

    /// <summary>
    /// Результат планирования следующего HTTP range-запроса.
    /// </summary>
    private readonly record struct DownloadPlan(long Start, int Length);

    // --- Section: Alignment Helpers ---

    /// <summary>Выравнивает <paramref name="value"/> вниз до кратного <paramref name="alignment"/>.</summary>
    private static long AlignDown(long value, int alignment) =>
        value - (value % alignment);

    /// <summary>Выравнивает <paramref name="value"/> вверх до кратного <paramref name="alignment"/>.</summary>
    private static int AlignUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    /// <summary>Выравнивает <paramref name="value"/> вверх до кратного <paramref name="alignment"/>.</summary>
    private static long AlignUp(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    // --- Section: Buffer Queries ---

    /// <summary>
    /// Возвращает число непрерывных байт от <paramref name="position"/> вперёд
    /// по локально доступным данным (RAM или диск).
    /// </summary>
    /// <param name="position">Стартовая позиция.</param>
    private long GetBufferedBytesAhead(long position)
    {
        long ramBytes = _ramCache.GetContiguousBytesFrom(position);
        long diskBytes = _cacheEntry?.GetContiguousDownloadedBytesFrom(position) ?? 0;
        return ramBytes >= diskBytes ? ramBytes : diskBytes;
    }

    /// <summary>
    /// Возвращает число непрерывных байт от <paramref name="position"/> вперёд,
    /// включая диапазоны, покрытые активными in-flight загрузками.
    /// </summary>
    /// <param name="position">Стартовая позиция.</param>
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

    // --- Section: Availability Queries ---

    /// <summary>
    /// Проверяет, доступен ли диапазон локально (RAM или диск).
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    private bool IsRangeLocallyAvailable(long position, int length)
    {
        if (length <= 0) return true;
        if (position < 0 || position >= _contentLength) return false;
        if (_ramCache.ContainsRange(position, length)) return true;
        return _cacheEntry?.IsRangeDownloaded(position, length) == true;
    }

    /// <summary>
    /// Проверяет, покрыт ли диапазон полностью локально либо активной in-flight загрузкой.
    /// </summary>
    /// <param name="position">Начало диапазона.</param>
    /// <param name="length">Длина диапазона.</param>
    /// <returns><c>true</c> если диапазон уже доступен или гарантированно качается.</returns>
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
    /// <returns><c>true</c> если диапазон полностью лежит внутри in-flight range.</returns>
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

    // --- Section: Read Length ---

    /// <summary>
    /// Возвращает выровненную длину чтения от <paramref name="position"/> до конца контента.
    /// </summary>
    /// <param name="position">Начальная позиция чтения.</param>
    /// <param name="minimumLength">Минимально допустимая длина.</param>
    private int GetAlignedReadLength(long position, int minimumLength)
    {
        if (position >= _contentLength) return 0;
        long remaining = _contentLength - position;
        int aligned = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        if (aligned > remaining) aligned = (int)remaining;
        return aligned;
    }

    // --- Section: Coverage Trim ---

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
    /// Длину первого непрерывного gap от <paramref name="start"/> до ближайшего уже доступного
    /// aligned диапазона. Если покрытия впереди нет — возвращает исходную <paramref name="length"/>.
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

    // --- Section: Throttle ---

    /// <summary>
    /// Вычисляет целевую скорость загрузки для throttle-режима (байт/сек).
    /// Возвращает <c>0</c> если throttle отключён (<see cref="StreamingConfig.ThrottleMultiplier"/> &lt;= 0).
    /// </summary>
    private double GetThrottleTargetBytesPerSec()
    {
        double multiplier = _config.ThrottleMultiplier;
        if (multiplier <= 0) return 0;
        double bitrateBytesPerSec = Math.Max(1, _bitrate) * 1000.0 / 8.0;
        return bitrateBytesPerSec * multiplier;
    }

    // --- Section: MAPO Planner ---

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

        // Throughput safety factor (модель THROUGHPUT, dash.js):
        // не претендуем на 100% канала, оставляем запас для параллельных запросов.
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

        // BDP floor (только в startup/seek фазе):
        // активируем при пустом буфере, чтобы максимально загрузить канал за один RTT.
        bool isStartupOrSeekPhase = currentBufferSec < (_config.BdpFloorMaxBufferMs / 1000.0);
        if (estimatedBandwidth > 0 && avgLatencyMs > 600 && isStartupOrSeekPhase)
        {
            double latencySec = avgLatencyMs / 1000.0;
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
            if (avgLatencyMs > 1200)
                criticalFloor = Math.Max(criticalFloor, Math.Min(maxLength, minLengthAligned * 2L));
            if (selectedBytes < criticalFloor) selectedBytes = criticalFloor;
        }

        if (selectedBytes > maxLength) selectedBytes = maxLength;
        selectedBytes = AlignUp(selectedBytes, _requestAlignmentBytes);

        long remaining = _contentLength - start;
        if (remaining <= 0) return new DownloadPlan(start, 0);

        if (selectedBytes > remaining) selectedBytes = remaining;
        if (selectedBytes < minimumLength)
            selectedBytes = Math.Min(AlignUp(minimumLength, _requestAlignmentBytes), remaining);

        int plannedLength = (int)selectedBytes;

        int trimmedLength = TrimLengthToFirstKnownCoverage(start, plannedLength, includeInflight: true);
        if (trimmedLength > 0 && trimmedLength < plannedLength)
            plannedLength = trimmedLength;

        return new DownloadPlan(start, plannedLength);
    }
}