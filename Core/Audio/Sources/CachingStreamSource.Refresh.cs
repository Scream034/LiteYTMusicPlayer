using System.Net.Http.Headers;
using LMP.Core.Audio.Http;
using LMP.Core.Exceptions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    // --- UrlRefreshOutcome ---

    /// <summary>
    /// Результат попытки обновления stream URL.
    /// </summary>
    private enum UrlRefreshOutcome
    {
        /// <summary>URL успешно обновлён и готов к использованию.</summary>
        Success,

        /// <summary>
        /// URL получен, но срок действия всё ещё истёк — session cache вернул старый manifest.
        /// </summary>
        StaleToken,

        /// <summary>URL не изменился вообще — refresher вернул null или тот же URL.</summary>
        NoChange,

        /// <summary>
        /// Circuit breaker открыт: слишком много последовательных неудачных refresh.
        /// Дальнейшие попытки бессмысленны.
        /// </summary>
        CircuitOpen
    }

    // --- URL Freshness Check ---

    /// <summary>
    /// Проактивно проверяет срок жизни URL потока и обновляет его при необходимости до старта чтения.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    public async Task EnsureStreamFreshnessAsync(CancellationToken ct)
    {
        if (_disposed || IsFullyBuffered) return;

        if (UrlEx.IsUrlExpiredOrExpiringSoon(_currentUrl, TimeSpan.FromMinutes(5)))
        {
            Log.Info($"[CachingSource] [{_trackId}] URL expiring soon on resume. Executing proactive refresh...");
            try
            {
                await CoordinatedRefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"[CachingSource] [{_trackId}] Proactive refresh on resume failed: {ex.Message}");
            }
        }
    }

    // --- URL Refresh ---

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

    /// <summary>
    /// Координирует обновление stream URL при получении HTTP 403 или обнаружении истёкшего TTL.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Исход попытки обновления URL.</returns>
    /// <exception cref="ChunkDownloadFatalException">
    /// Выбрасывается когда circuit breaker открыт (слишком много failures подряд).
    /// </exception>
    private async Task<UrlRefreshOutcome> CoordinatedRefreshAsync(CancellationToken ct)
    {
        // Circuit Breaker: защита от bot detection при повторных безнадёжных запросах.
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

        bool isInitiator;
        try
        {
            isInitiator = await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return UrlRefreshOutcome.NoChange;
        }

        if (!isInitiator)
            return await WaitForConcurrentRefreshAsync(ct).ConfigureAwait(false);

        try
        {
            // Рефреш требует времени (QuickJS + BotGuard ~3-5 сек). 
            // Используем lifetime токен с щедрым таймаутом, чтобы отмена отдельного Range/Seek 
            // не срывала получение URL на 99% готовности.
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts?.Token ?? CancellationToken.None);
            refreshCts.CancelAfter(TimeSpan.FromSeconds(20));

            return await ExecuteRefreshAsync(refreshCts.Token).ConfigureAwait(false);
        }
        finally
        {
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
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            _refreshLock.Release();
        }
        catch (ObjectDisposedException)
        {
            return UrlRefreshOutcome.NoChange;
        }

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

        // Небольшой delay: даём CDN время "переварить" новый URL.
        await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);

        // Успех фиксируется ТОЛЬКО если URL реально присутствует и 403 сброшен
        bool concurrentRefreshSucceeded = !string.IsNullOrWhiteSpace(_currentUrl)
                                       && Volatile.Read(ref _consecutive403Count) == 0;

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
        // Cooldown: защита от refresh-шторма при параллельных 403.
        var elapsed = DateTime.UtcNow - _lastRefreshTime;
        if (elapsed.TotalMilliseconds < _config.RefreshCooldownMs)
        {
            int waitMs = _config.RefreshCooldownMs - (int)elapsed.TotalMilliseconds;
            Log.Debug($"[CachingSource] [{_trackId}] Refresh cooldown: waiting {waitMs}ms");
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }

        string? urlBeforeRefresh = _currentUrl;
        string? nTokenBefore = UrlEx.TryGetQueryParameterValue(urlBeforeRefresh, "n");

        Log.Info($"[CachingSource] [{_trackId}] Executing URL refresh. " +
                $"Current n-token: {nTokenBefore?[..Math.Min(nTokenBefore.Length, 10)] ?? "MISSING"}...");

        await RefreshUrlAsync(ct).ConfigureAwait(false);
        _lastRefreshTime = DateTime.UtcNow;

        string? urlAfterRefresh = _currentUrl;
        string? nTokenAfter = UrlEx.TryGetQueryParameterValue(urlAfterRefresh, "n");

        bool urlActuallyChanged =
            !string.Equals(urlBeforeRefresh, urlAfterRefresh, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(urlAfterRefresh);

        if (!urlActuallyChanged)
        {
            // NoChange = transient (timeout, network error, API unavailable) — не circuit break.
            Log.Warn($"[CachingSource] [{_trackId}] Refresh returned no new URL " +
                    $"(transient, circuit breaker not incremented)");
            return UrlRefreshOutcome.NoChange;
        }

        bool hasValidNewExpire = UrlEx.TryGetExpireUtc(urlAfterRefresh, out var expAfter)
            && expAfter > DateTime.UtcNow.AddMinutes(5);

        bool nTokenChanged = !string.IsNullOrEmpty(nTokenAfter)
                            && !string.Equals(nTokenBefore, nTokenAfter, StringComparison.Ordinal);

        // Если expire в будущем или n-token изменился — URL гарантированно валиден.
        // StaleToken фиксируется ТОЛЬКО если новый URL пришёл с уже истёкшим/устаревшим expire.
        if (!hasValidNewExpire && !nTokenChanged && !string.IsNullOrEmpty(nTokenBefore))
        {
            int failures = Interlocked.Increment(ref _consecutiveRefreshFailures);
            Log.Warn($"[CachingSource] [{_trackId}] Refresh got URL with unchanged n-token and expired TTL " +
                    $"— likely stale session cache (failure {failures}/{MaxRefreshFailuresBeforeCircuitBreak})");

            await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
            return UrlRefreshOutcome.StaleToken;
        }

        Volatile.Write(ref _consecutive403Count, 0);
        Volatile.Write(ref _consecutiveRefreshFailures, 0);

        Log.Info($"[CachingSource] [{_trackId}] URL refresh successful. " +
                $"New n-token: {nTokenAfter?[..Math.Min(nTokenAfter.Length, 10)] ?? "?"}..., expire={expAfter:O}");

        await Task.Delay(_config.PostRefreshDelayMs, ct).ConfigureAwait(false);
        return UrlRefreshOutcome.Success;
    }

    // --- EnsureUrlAvailable ---

    /// <summary>
    /// Гарантирует наличие валидного continuation URL перед сетевой загрузкой.
    /// Реализует single-flight модель: если URL уже есть — возвращает сразу,
    /// если нет — либо ждёт внешнего attach, либо запускает самостоятельный acquire.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns><c>true</c> если URL доступен; <c>false</c> если получить не удалось.</returns>
    private async Task<bool> EnsureUrlAvailableAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_currentUrl))
            return true;

        Log.Debug($"[CachingSource] [{_trackId}] EnsureUrlAvailable: no URL yet, " +
                $"hasAcquirer={_urlAcquirer != null}");

        Task<string?> waitTask;
        bool isInitiator = false;

        lock (_continuationLock)
        {
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                return true;

            if (_continuationUrlTcs != null)
            {
                waitTask = _continuationUrlTcs.Task;
            }
            else
            {
                var tcs = new TaskCompletionSource<string?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _continuationUrlTcs = tcs;
                waitTask = tcs.Task;
                isInitiator = true;
            }
        }

        if (isInitiator && _urlAcquirer != null)
            _ = ResolveContinuationUrlSingleFlightAsync(ct);

        try
        {
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return !string.IsNullOrWhiteSpace(_currentUrl);
        }

        Log.Info($"[CachingSource] [{_trackId}] EnsureUrlAvailable resolved: " +
                $"hasUrl={!string.IsNullOrWhiteSpace(_currentUrl)}");

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

    // --- 403 Diagnostics ---

    /// <summary>
    /// Логирует диагностические параметры 403-ответа: n-token, c-param, UA, тело ответа.
    /// </summary>
    private async Task LogAndDiagnose403Async(
        int logicalIndex, HttpRequestMessage request, HttpResponseMessage response)
    {
        int count = Volatile.Read(ref _consecutive403Count);
        var nParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(_currentUrl, "c");
        var reqUa = request.Headers.UserAgent.ToString();

        Log.Warn($"[CachingSource] 403 DIAGNOSTIC range@{logicalIndex} (consecutive={count})");
        Log.Warn($"[CachingSource]   c={cParam ?? "?"}, UA={reqUa[..Math.Min(reqUa.Length, 50)]}...");
        Log.Warn($"[CachingSource]   n-token: {nParam ?? "MISSING"} " +
                 $"(len={nParam?.Length ?? 0}, " +
                 $"looks_encrypted={nParam?.Length is > 15 and < 25})");

        if (response.Headers.TryGetValues("X-Restrict-Formats-Hint", out var hints))
            Log.Warn($"[CachingSource]   Restrict-Hint: {string.Join(", ", hints)}");

        string responseBody = "";
        try
        {
            responseBody = await response.Content
                .ReadAsStringAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { }

        if (responseBody.Length > 0)
            Log.Warn($"[CachingSource]   Body: {responseBody[..Math.Min(responseBody.Length, 200)]}");

        Log.Warn("[CachingSource]══");
    }

    // --- HTTP Request Building ---

    /// <summary>
    /// Строит HTTP range-запрос с учётом хоста (YouTube vs generic CDN).
    /// </summary>
    private HttpRequestMessage CreateRangeRequest(int logicalIndex, long start, long end, int rn)
    {
        bool isYouTube = _currentUrl.Contains(
            "googlevideo.com/videoplayback", StringComparison.Ordinal);

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

    /// <summary>
    /// Логирует параметры подписанного URL (n-token, c-param, sig) для диагностики.
    /// </summary>
    private static void LogRangeRequestParams(int logicalIndex, string url)
    {
        var nParam = UrlEx.TryGetQueryParameterValue(url, "n");
        var cParam = UrlEx.TryGetQueryParameterValue(url, "c");
        var sigParam = UrlEx.TryGetQueryParameterValue(url, "sig");
        Log.Debug($"[CachingSource] Range {logicalIndex} URL: {url[..Math.Min(url.Length, 300)]}");
        Log.Debug($"[CachingSource] Range {logicalIndex} params: " +
                  $"c={cParam ?? "MISSING"}, " +
                  $"n={nParam?[..Math.Min(nParam.Length, 15)] ?? "MISSING"}..., " +
                  $"sig={(sigParam is not null ? $"{sigParam.Length}chars" : "MISSING")}");
    }

    /// <summary>
    /// Добавляет YouTube-специфичные параметры <c>rn</c> и <c>rbuf</c> к URL.
    /// </summary>
    private static string BuildYouTubeRangeUrl(string baseUrl, int rn)
    {
        string url = UrlEx.SetQueryParameter(baseUrl, "rn", rn.ToString());
        url = UrlEx.SetQueryParameter(url, "rbuf", "0");
        return url;
    }
}