using System.Buffers;
using LMP.Core.Exceptions;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- Section: RangeDownloadResult ---

    /// <summary>
    /// Исход попытки загрузки HTTP range-диапазона.
    /// </summary>
    private enum RangeDownloadResult
    {
        /// <summary>Диапазон успешно загружен и доступен локально.</summary>
        Success,

        /// <summary>Сервер вернул HTTP 403 Forbidden — URL истёк.</summary>
        Forbidden403,

        /// <summary>Транзиентная сетевая ошибка (timeout, socket, IO).</summary>
        NetworkError,

        /// <summary>Неустранимая ошибка (UMP-формат, circuit breaker).</summary>
        Fatal,

        /// <summary>Операция отменена (epoch change, dispose, внешний CancellationToken).</summary>
        Cancelled,

        /// <summary>Не удалось получить слот семафора за отведённое время.</summary>
        SlotTimeout,

        /// <summary>Позиция выходит за пределы контента.</summary>
        OutOfRange
    }

    // --- Section: Adaptive Metrics ---

    /// <summary>
    /// Сохраняет замер RTT через скользящее окно из трёх значений.
    /// </summary>
    /// <param name="latencyMs">Измеренная задержка в миллисекундах.</param>
    private void SaveLatency(double latencyMs)
    {
        double currentAverage;
        lock (_latencyLock)
        {
            _latency2 = _latency1;
            _latency1 = _latency0;
            _latency0 = latencyMs;
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
                _estimatedBandwidthBytesPerSec = speedBytesPerSec;
                _bandwidthSampleCount = 1;
            }
            else
            {
                _bandwidthSampleCount++;

                double weight;
                if (_bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount)
                {
                    weight = _config.BandwidthBootstrapWeight;
                }
                else
                {
                    // Крупные блоки (≥ 256 KB) несут больше информации о реальной скорости.
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
            $"phase={(_bandwidthSampleCount <= _config.BandwidthBootstrapSampleCount ? "bootstrap" : "steady")})");
    }

    // --- Section: ReadAtAsync ---

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

    /// <summary>
    /// Читает данные с произвольной позиции, используя RAM-кэш, диск и сеть в порядке приоритета.
    /// Реализует бесконечный retry для transient network errors с exponential backoff.
    /// </summary>
    /// <param name="position">Абсолютная позиция чтения в контенте.</param>
    /// <param name="buffer">Буфер назначения.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Количество прочитанных байт; <c>0</c> при достижении конца контента.</returns>
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
                            epochRetries++;
                            if (epochRetries >= ReadAtMaxEpochRetries)
                                throw CreateReadAtFatalException(position);
                            await Task.Delay(ReadAtEpochRetryDelayMs, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // ObjectDisposed от Rebuild — трактуем как transient сетевую ошибку.
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

    /// <summary>
    /// Пытается загрузить диапазон из дискового кэша в RAM и прочитать данные из него.
    /// </summary>
    /// <param name="position">Абсолютная позиция начала чтения.</param>
    /// <param name="minimumLength">Минимально необходимый объём данных.</param>
    /// <param name="target">Буфер назначения.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Количество прочитанных байт; <c>0</c> если диапазон недоступен на диске.</returns>
    private async Task<int> TryLoadRangeFromDiskAsync(
        long position, int minimumLength, Memory<byte> target, CancellationToken ct)
    {
        if (_cacheEntry == null) return 0;
        if (!_cacheEntry.TryGetContainingRange(position, out long rangeStart, out long rangeEndExclusive))
            return 0;

        long loadStart = AlignDown(position, _requestAlignmentBytes);
        if (loadStart < rangeStart) loadStart = rangeStart;

        int desiredLength = AlignUp(Math.Max(minimumLength, _config.MinRequestSizeBytes), _requestAlignmentBytes);
        long available = rangeEndExclusive - loadStart;
        if (available <= 0) return 0;
        if (desiredLength > available) desiredLength = (int)available;

        var diskResult = await _cacheManager
            .ReadRangeAsync(_cacheKey, loadStart, desiredLength, ct)
            .ConfigureAwait(false);

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

    // --- Section: EnsureRangeAsync ---

    /// <summary>
    /// Гарантирует наличие диапазона данных, предотвращая бесконечные ретраи (Retry Storm).
    /// <para>
    /// При 403 инициирует <see cref="CoordinatedRefreshAsync"/>; при network error —
    /// quadratic backoff; при fatal — пробрасывает <see cref="ChunkDownloadFatalException"/>.
    /// </para>
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

        // Source-level circuit breaker: не входим в retry loop если breaker открыт.
        if (IsSourceCircuitBreakerOpen(out int cbRemainingMs))
        {
            int cbDelay = Math.Min(cbRemainingMs, 1000);
            await Task.Delay(cbDelay, ct).ConfigureAwait(false);
            return RangeDownloadResult.NetworkError;
        }

        int maxAttempts = _config.MaxNetworkRetries;
        int chunkIoExceptions = 0;
        bool warningPublished = false;
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
                await WaitForActiveDownloadAsync(actual.LazyTask.Value, ct).ConfigureAwait(false);
                continue;
            }

            RangeDownloadResult result;
            try
            {
                result = await actual.LazyTask.Value.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Epoch сменился (seek отменил старые загрузки) — нормальный transient.
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

                case RangeDownloadResult.Forbidden403:
                    {
                        if (isCritical && !warningPublished)
                        {
                            warningPublished = true;
                            PublishSourceWarning(
                                new UnauthorizedAccessException(
                                    $"Critical range {plan.Start}-{plan.Start + plan.Length - 1L} " +
                                    $"received HTTP 403 and requires stream URL refresh"));
                        }

                        // Отменяем ВСЕ параллельные preload: они используют тот же мёртвый URL.
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
                            throw;
                        }

                        switch (outcome)
                        {
                            case UrlRefreshOutcome.Success:
                                consecutiveStaleRefreshes = 0;
                                Log.Info($"[CachingSource] [{_trackId}] URL refreshed, retrying range {plan.Start}");
                                continue;

                            case UrlRefreshOutcome.StaleToken:
                                consecutiveStaleRefreshes++;
                                Log.Warn($"[CachingSource] [{_trackId}] Stale n-token after refresh " +
                                        $"({consecutiveStaleRefreshes}/{MaxStaleRefreshesBeforeGivingUp}). " +
                                        $"Backing off before retry...");

                                if (consecutiveStaleRefreshes >= MaxStaleRefreshesBeforeGivingUp)
                                {
                                    Log.Error($"[CachingSource] [{_trackId}] Too many stale refreshes. " +
                                            $"Escalating to fatal.");
                                    throw CreateReadAtFatalException(position);
                                }

                                // Exponential backoff: 1s, 2s, 4s
                                int backoffMs = (int)Math.Pow(2, consecutiveStaleRefreshes - 1) * 1000;
                                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                                continue;

                            case UrlRefreshOutcome.NoChange:
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

        return RangeDownloadResult.NetworkError;
    }

    // --- Section: Source Circuit Breaker ---

    /// <summary>
    /// Проверяет, открыт ли source-level circuit breaker.
    /// При истечении backoff переходит в half-open (пропускает один запрос).
    /// </summary>
    /// <param name="remainingMs">Оставшийся backoff в мс (0 если закрыт или half-open).</param>
    /// <returns><c>true</c> если запрос должен быть отклонён.</returns>
    private bool IsSourceCircuitBreakerOpen(out int remainingMs)
    {
        lock (_circuitBreakerLock)
        {
            remainingMs = 0;
            if (!_circuitBreakerIsOpen) return false;

            long elapsed = Environment.TickCount64 - _circuitBreakerOpenedAtTick;
            remainingMs = _circuitBreakerBackoffMs - (int)elapsed;

            if (remainingMs <= 0)
            {
                _circuitBreakerIsOpen = false;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Регистрирует успешную сетевую операцию: сбрасывает счётчик, закрывает breaker.
    /// </summary>
    private void OnSourceNetworkSuccess()
    {
        bool wasActive;
        lock (_circuitBreakerLock)
        {
            wasActive = _circuitBreakerIsOpen || _consecutiveSourceNetworkFailures > 0;
            _consecutiveSourceNetworkFailures = 0;
            _circuitBreakerIsOpen = false;
            _circuitBreakerBackoffMs = 0;
        }

        if (wasActive)
            Log.Info($"[CachingSource] [{_trackId}] Source circuit breaker CLOSED — network recovered");
    }

    /// <summary>
    /// Регистрирует сетевую ошибку. При достижении порога открывает breaker с exponential backoff.
    /// </summary>
    private void OnSourceNetworkFailure()
    {
        lock (_circuitBreakerLock)
        {
            int failures = ++_consecutiveSourceNetworkFailures;

            if (!_circuitBreakerIsOpen && failures >= SourceCircuitBreakerThreshold)
            {
                _circuitBreakerIsOpen = true;
                _circuitBreakerOpenedAtTick = Environment.TickCount64;
                _circuitBreakerBackoffMs = SourceCircuitBreakerInitialBackoffMs;

                Log.Warn($"[CachingSource] [{_trackId}] Source circuit breaker OPEN: " +
                         $"{failures} consecutive failures, backoff={_circuitBreakerBackoffMs}ms");
            }
            else if (_circuitBreakerIsOpen)
            {
                _circuitBreakerOpenedAtTick = Environment.TickCount64;
                _circuitBreakerBackoffMs = Math.Min(
                    _circuitBreakerBackoffMs * 2,
                    SourceCircuitBreakerMaxBackoffMs);

                Log.Debug($"[CachingSource] [{_trackId}] Circuit breaker re-opened: " +
                          $"backoff={_circuitBreakerBackoffMs}ms");
            }
        }
    }

    // --- Section: DownloadRangeCoreAsync ---

    /// <summary>
    /// Оркестрирует загрузку диапазона: circuit breaker → слот семафора → URL guard → HTTP.
    /// </summary>
    private async Task<RangeDownloadResult> DownloadRangeCoreAsync(
        DownloadPlan plan, CancellationToken ct, bool isCritical)
    {
        if (_disposed) return RangeDownloadResult.Cancelled;
        if (IsRangeLocallyAvailable(plan.Start, plan.Length)) return RangeDownloadResult.Success;

        if (IsSourceCircuitBreakerOpen(out int remainingBackoffMs))
        {
            int delay = Math.Min(remainingBackoffMs, 500);
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return RangeDownloadResult.NetworkError;
        }

        bool gotSlot = false;
        try
        {
            if (!isCritical)
            {
                gotSlot = await _downloadSlots
                    .WaitAsync(_config.DownloadSlotTimeoutMs, ct)
                    .ConfigureAwait(false);
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
                    OnSourceNetworkFailure();
                    return ct.IsCancellationRequested
                        ? RangeDownloadResult.Cancelled
                        : RangeDownloadResult.NetworkError;
                }
            }

            var result = await DownloadRangeHttpAsync(plan, ct).ConfigureAwait(false);

            switch (result)
            {
                case RangeDownloadResult.Success:
                case RangeDownloadResult.OutOfRange:
                    OnSourceNetworkSuccess();
                    break;
                case RangeDownloadResult.NetworkError:
                    OnSourceNetworkFailure();
                    break;
            }

            return result;
        }
        catch (ChunkDownloadFatalException) { throw; }
        catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)
        { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException)
        { return RangeDownloadResult.NetworkError; }
        catch (OperationCanceledException)
        { return RangeDownloadResult.Cancelled; }
        catch (Exception) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (Exception ex)
        {
            Log.Warn($"[CachingSource] Range {plan.Start} unexpected: {ex.Message}");
            OnSourceNetworkFailure();
            return RangeDownloadResult.NetworkError;
        }
        finally
        {
            if (gotSlot)
            {
                try { _downloadSlots.Release(); }
                catch (ObjectDisposedException) { }
            }
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

    // --- Section: DownloadRangeHttpAsync ---

    /// <summary>
    /// Выполняет HTTP range-запрос, записывает данные в RAM и на диск.
    /// </summary>
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
            response = await CurrentHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode) SaveLatency(sw.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested || _disposed)
        { return RangeDownloadResult.Cancelled; }
        catch (HttpRequestException ex) when (IsCancelledSendFailure(ex, ct, _disposed))
        { return RangeDownloadResult.Cancelled; }
        catch (TaskCanceledException)
        { return RangeDownloadResult.NetworkError; }
        catch (HttpRequestException)
        { return RangeDownloadResult.NetworkError; }

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

            // После первого успешного ответа запоминаем CDN-ноду для speculative pre-warm.
            Http.CdnConnectionPreWarmer.RecordHost(_currentUrl);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("yt-ump") == true)
                throw new ChunkDownloadFatalException(
                    "YouTube returned encrypted UMP format",
                    chunkIndex: logicalIndex,
                    consecutiveFailures: 0,
                    reason: ChunkDownloadFailureReason.UmpFormat,
                    trackId: _trackId);

            response.EnsureSuccessStatusCode();

            try
            {
                using var contentStream = await response.Content
                    .ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);

                IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(plan.Length);
                int actualLength;
                var bodySw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    actualLength = await ReadStreamFullyAsync(
                        contentStream, memoryOwner.Memory[..plan.Length], ct)
                        .ConfigureAwait(false);
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
                catch (Exception ex) when (
                    ct.IsCancellationRequested || _disposed
                    || ex is IOException
                    || ex is System.Net.Sockets.SocketException)
                {
                    _lastDownloadException = ex;
                    memoryOwner.Dispose();

                    if (ct.IsCancellationRequested || _disposed)
                        return RangeDownloadResult.Cancelled;

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

                // Игнорируем микро-замеры (< 16KB или < 20мс) — ложные всплески TCP slow-start.
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

                // disk copy создаётся всегда: overlap-case иначе превращается в
                // "скачал и выбросил" с бесконечным повтором того же диапазона.
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
                    _ramCache.Trim(
                        Volatile.Read(ref _currentReadOffset),
                        _config.RamEvictionWindowBytes,
                        _config.MaxRamBytes);

                return RangeDownloadResult.Success;
            }
            catch (OperationCanceledException)
            {
                return RangeDownloadResult.Cancelled;
            }
        }
    }

    /// <summary>
    /// Читает поток полностью в буфер, пока не достигнут его конец или EOF потока.
    /// </summary>
    private static async ValueTask<int> ReadStreamFullyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
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

    // --- Section: Network Retry Helpers ---

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
    /// Публикует событие восстановления сети после stall.
    /// </summary>
    private void PublishNetworkRecovered()
    {
        Log.Info($"[CachingSource] [{_trackId}] Network recovered — data flow restored");
        try { OnNetworkRecovered?.Invoke(_trackId); }
        catch (Exception ex) { Log.Warn($"[CachingSource] NetworkRecovered handler: {ex.Message}"); }
    }
}