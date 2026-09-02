using LMP.Core.Youtube.Bridge;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Exceptions;
using LMP.Core.Youtube.Utils;
using LMP.Core.Helpers.Extensions;
using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Videos;

/// <summary>
/// Единый контроллер для взаимодействия с API YouTube (получение данных о видео, PlayerResponse, DASH/HLS манифестов).
/// </summary>
internal partial class VideoController(HttpClient http, PlayerContextManager playerManager)
{
    #region Bot Detection State

    private static DateTime _lastBotDetection = DateTime.MinValue;
    private static int _consecutiveFailures;
    private static readonly SemaphoreSlim _requestThrottle = new(1, 1);
    private static readonly Lock _stateLock = new();

    /// <summary>
    /// Количество BotDetection-сбоев ANDROID_VR в текущей сессии.
    /// Используется для пропуска клиента, стабильно блокируемого сервером,
    /// без потери времени на roundtrip (~700 мс) при каждом force-refresh.
    /// Сбрасывается при успешном ответе ANDROID_VR или явном <see cref="ResetBotDetectionState"/>.
    /// </summary>
    private static volatile int _androidVrSessionBotDetections;
    private static DateTime _androidVrLastBotDetection = DateTime.MinValue;
    private const int AndroidVrCooldownSeconds = 300; // 5 минут

    public static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(2);

    public static bool IsInCooldown
    {
        get
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures < 3) return false;
                var elapsed = DateTime.UtcNow - _lastBotDetection;
                return elapsed < CooldownDuration;
            }
        }
    }

    public static TimeSpan GetRemainingCooldown()
    {
        lock (_stateLock)
        {
            if (_consecutiveFailures < 3) return TimeSpan.Zero;

            var elapsed = DateTime.UtcNow - _lastBotDetection;
            var remaining = CooldownDuration - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public static void ResetBotDetectionState()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _androidVrSessionBotDetections = 0;
            _androidVrLastBotDetection = DateTime.MinValue;
            _lastBotDetection = DateTime.MinValue;
            Log.Info("[VideoController] Bot detection state reset");
        }
    }

    public static void ThrowIfInCooldown()
    {
        if (IsInCooldown)
        {
            var remaining = GetRemainingCooldown();
            throw new BotDetectionException(
                $"Rate limited by YouTube. Please wait {remaining.TotalSeconds:F0} seconds.",
                remaining);
        }
    }

    #endregion

    #region Throttle

    private static TimeSpan GetThrottleDelay()
    {
        int failures;
        lock (_stateLock) failures = _consecutiveFailures;

        return failures switch
        {
            0 => TimeSpan.FromMilliseconds(150),
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(5)
        };
    }

    #endregion

    private readonly PlayerContextManager _playerManager = playerManager;

    protected HttpClient Http { get; } = http;

    /// <summary>
    /// Получает HTML страницу просмотра видеоролика с поддержкой повторных попыток при сетевых сбоях.
    /// </summary>
    public async ValueTask<VideoWatchPage> GetVideoWatchPageAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        return await ResilienceExecutor.ExecuteWithRetryAsync(async () =>
        {
            var rawHtml = await Http.GetStringAsync(
                $"https://www.youtube.com/watch?v={videoId}&bpctr=9999999999",
                cancellationToken).ConfigureAwait(false);

            var watchPage = VideoWatchPage.TryParse(rawHtml);

            if (watchPage is null)
            {
                throw new YoutubeExplodeException("Video watch page is broken. Please try again in a few minutes.");
            }

            if (!watchPage.IsAvailable)
            {
                throw new VideoUnavailableException($"Video '{videoId}' is not available.");
            }

            return watchPage;
        }, maxRetries: 5, cancellationToken).ConfigureAwait(false);
    }

    #region GetPlayerResponse

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var clientName = YoutubeClientUtils.GetClientApiName(YoutubeClientUtils.CurrentProfile);
        return await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseWithClientAsync(
       VideoId videoId,
       string clientName,
       CancellationToken cancellationToken,
       string? signatureTimestamp = null)
    {
        ThrowIfInCooldown();

        await _requestThrottle.WaitAsync(cancellationToken);
        try
        {
            var delay = GetThrottleDelay();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
        finally { _requestThrottle.Release(); }

        Log.Info($"GetPlayerResponse START ({clientName}): {videoId}");

        var visitorData = await YoutubeClientUtils.EnsureVisitorDataAsync(ct: cancellationToken).ConfigureAwait(false);

        if (signatureTimestamp == null && clientName is "WEB" or "WEB_REMIX" or "TVHTML5_SIMPLY_EMBEDDED_PLAYER")
        {
            signatureTimestamp = await ResolveSignatureTimestampAsync(cancellationToken);
        }

        var playerUrl = clientName == "WEB_REMIX"
            ? "https://music.youtube.com/youtubei/v1/player?prettyPrint=false"
            : "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";

        using var request = new HttpRequestMessage(HttpMethod.Post, playerUrl);
        request.Headers.Add("User-Agent", YoutubeClientUtils.GetUserAgentForClient(clientName));

        bool isMobileClient = clientName is "ANDROID_VR" or "ANDROID_MUSIC" or "IOS" or
                      "TVHTML5_SIMPLY_EMBEDDED_PLAYER" or "ANDROID_TESTSUITE";

        if (isMobileClient)
        {
            request.Options.Set(YoutubeHttpHandler.IsMobileClient, true);
            request.Options.Set(YoutubeHttpHandler.IsPlayerContext, true);
        }
        else
        {
            request.Options.Set(YoutubeHttpHandler.IsMobileClient, false);
        }

        string jsonBody = YoutubeClientUtils.GeneratePlayerContextForClient(
            clientName, videoId.Value, visitorData, signatureTimestamp);

        request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            Log.Warn($"[VideoController] [{videoId}] {clientName} HTTP {statusCode}");

            if (statusCode == 403)
            {
                throw new StreamUnavailableException(
                    $"HTTP 403 Forbidden for video {videoId} via {clientName}",
                    videoId.Value,
                    StreamUnavailableReason.Forbidden403,
                    httpStatusCode: 403);
            }

            throw new YoutubeExplodeException($"HTTP {response.StatusCode} for client {clientName}");
        }

        var playerResponse = PlayerResponse.Parse(content);

        // Bot detection tracking ПЕРЕД LoginRequired
        // Обеспечивает инкремент _consecutiveFailures для LOGIN_REQUIRED + "bot"
        TrackBotDetection(playerResponse);

        if (playerResponse.IsLoginRequired)
        {
            var reason = playerResponse.LoginRequiredReason;

            Log.Warn($"[VideoController] [{videoId}] LOGIN_REQUIRED via {clientName}: " +
                     $"reason={reason}, raw=\"{playerResponse.PlayabilityError}\"");

            throw new LoginRequiredException(
                $"Video {videoId} requires login: {playerResponse.LoginRequiredReason}",
                videoId.Value,
                reason);
        }

        return playerResponse;
    }

    /// <summary>
    /// Извлекает signatureTimestamp из единого кэша <see cref="PlayerContextManager"/>.
    /// При сетевом таймауте делает fallback на дисковый кэш, если он доступен.
    /// Пробрасывает сетевые ошибки и отмены, чтобы вызывающий код
    /// мог корректно определить причину сбоя.
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Строка signatureTimestamp или <c>null</c> при некритичной ошибке.</returns>
    private async ValueTask<string?> ResolveSignatureTimestampAsync(CancellationToken ct)
    {
        // 1. Быстрый путь: in-memory кэш
        var cached = _playerManager.GetCachedSignatureTimestamp();
        if (!string.IsNullOrEmpty(cached)) return cached;

        try
        {
            var context = await _playerManager.GetOrLoadAsync(ct).ConfigureAwait(false);

            var sts = context.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(context.BaseJs))
            {
                sts = YoutubeAstSolver.ExtractSts(context.BaseJs);
                Log.Debug($"[VideoController] STS resolved via BaseJs fallback: {sts}");
            }

            _playerManager.SetCachedSignatureTimestamp(sts);
            return sts;
        }
        catch (YoutubeNetworkException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Внутренний таймаут GetOrLoadAsync (не отмена вызывающего кода) —
            // пробуем дисковый кэш, который мог остаться от предыдущей сессии
            Log.Warn("[VideoController] GetOrLoadAsync timed out internally, trying disk cache fallback");
            return TryFallbackSignatureTimestamp();
        }
        catch (OperationCanceledException)
        {
            // Отмена вызывающего кода — пробрасываем
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"[VideoController] Failed to get signatureTimestamp: {ex.Message}");
            return TryFallbackSignatureTimestamp();
        }
    }

    /// <summary>
    /// Пытается извлечь signatureTimestamp из дискового кэша PlayerContext
    /// без обращения к сети. Возвращает <c>null</c> если кэш отсутствует.
    /// </summary>
    private string? TryFallbackSignatureTimestamp()
    {
        // Повторная проверка in-memory — мог быть заполнен параллельным потоком
        var cached = _playerManager.GetCachedSignatureTimestamp();
        if (!string.IsNullOrEmpty(cached))
        {
            Log.Debug($"[VideoController] STS fallback: found in memory cache: {cached}");
            return cached;
        }

        // Пробуем загрузить из дискового кэша без сети
        var diskContext = PlayerContextManager.TryLoadFromDiskCache();
        if (diskContext != null)
        {
            var sts = diskContext.Sts;

            if (string.IsNullOrEmpty(sts) && !string.IsNullOrEmpty(diskContext.BaseJs))
            {
                sts = YoutubeAstSolver.ExtractSts(diskContext.BaseJs);
            }

            if (!string.IsNullOrEmpty(sts))
            {
                _playerManager.SetCachedSignatureTimestamp(sts);
                Log.Info($"[VideoController] STS fallback: resolved from disk cache: {sts}");
                return sts;
            }
        }

        Log.Warn("[VideoController] STS fallback: no disk cache available");
        return null;
    }

    #endregion

    #region Bot Detection Tracking

    /// <summary>
    /// Проверяет, указывает ли сообщение об ошибке плейера на Bot Detection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBotDetectionError(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;

        return error.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static void TrackBotDetection(PlayerResponse response)
    {
        if (IsBotDetectionResponse(response))
        {
            lock (_stateLock)
            {
                _consecutiveFailures++;
                _lastBotDetection = DateTime.UtcNow;

                if (_consecutiveFailures == 1)
                {
                    Log.Warn("[VideoController] ⚠️ Bot detection triggered — slowing down requests");
                }
                else if (_consecutiveFailures >= 3)
                {
                    Log.Error($"[VideoController] ❌ Multiple bot detections ({_consecutiveFailures}) — cooldown active");
                }
            }
        }
        else if (response.IsPlayable)
        {
            lock (_stateLock)
            {
                if (_consecutiveFailures > 0)
                {
                    Log.Info($"[VideoController] ✓ Bot detection cleared after {_consecutiveFailures} failures");
                    _consecutiveFailures = 0;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBotDetectionResponse(PlayerResponse response)
    {
        return IsBotDetectionError(response.PlayabilityError);
    }

    #endregion

    #region Fallback & DASH Methods

    public async ValueTask<(PlayerResponse Response, string ClientName)> GetPlayerResponseWithFallbackAsync(
       VideoId videoId,
       CancellationToken cancellationToken,
       bool isAuthenticated = false)
    {
        var clients = YoutubeClientUtils.GetStreamFallbackClients(isAuthenticated);
        var errors = new List<string>();
        var allBotDetection = true;
        bool hasLoginRequired = false;
        LoginRequiredException? loginException = null;

        bool hasNonNetworkFailure = false;
        YoutubeNetworkException? firstNetworkException = null;

        foreach (var clientName in clients)
        {
            // Пропускаем клиент, если он уже давал BotDetection в текущей сессии.
            // Это предотвращает ~700 мс блокировку WEB_REMIX при каждом force-refresh.
            if (string.Equals(clientName, "ANDROID_VR", StringComparison.Ordinal)
                && _androidVrSessionBotDetections > 0
                && (DateTime.UtcNow - _androidVrLastBotDetection).TotalSeconds < AndroidVrCooldownSeconds)
            {
                Log.Debug($"[VideoController] [{videoId}] ANDROID_VR skipped — " +
                        $"{_androidVrSessionBotDetections} BotDetection(s), " +
                        $"cooldown {AndroidVrCooldownSeconds - (int)(DateTime.UtcNow - _androidVrLastBotDetection).TotalSeconds}s remaining");
                errors.Add("ANDROID_VR: SKIPPED_BOT_DETECTION_COOLDOWN");
                allBotDetection = true;
                hasNonNetworkFailure = true;
                continue;
            }

            try
            {
                var response = await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);

                if (response.IsPlayable && HasAnyStream(response))
                {
                    // Успешный ответ ANDROID_VR — блокировка снята, СБРАСЫВАЕМ счётчик
                    if (string.Equals(clientName, "ANDROID_VR", StringComparison.Ordinal)
                        && _androidVrSessionBotDetections > 0)
                    {
                        _androidVrSessionBotDetections = 0;
                        _androidVrLastBotDetection = DateTime.MinValue;
                        Log.Debug("[VideoController] ANDROID_VR bot detection counter reset on success");
                    }

                    Log.Info($"[VideoController] [{videoId}] SUCCESS with {clientName}");
                    return (response, clientName);
                }

                var error = response.PlayabilityError ?? "Not playable / No streams";
                Log.Warn($"[VideoController] [{videoId}] {clientName}: {error}");
                errors.Add($"{clientName}: {error}");

                var reason = PlayabilityErrorClassifier.Classify(error, out _);
                if (reason is StreamUnavailableReason.CopyrightBlocked or
                             StreamUnavailableReason.RegionBlocked or
                             StreamUnavailableReason.Private or
                             StreamUnavailableReason.Removed)
                {
                    Log.Info($"[VideoController] [{videoId}] Hard legal restriction detected ({reason}). Fast-failing further clients.");
                    throw new StreamUnavailableException(error, videoId.Value, reason, wasHlsFallback: false);
                }

                if (!IsBotDetectionResponse(response))
                    allBotDetection = false;

                hasNonNetworkFailure = true;
            }
            catch (LoginRequiredException ex)
            {
                if (ex.Reason == LoginRequiredReason.BotDetection)
                {
                    // --- ANDROID_VR BotDetection Tracking ---
                    if (string.Equals(clientName, "ANDROID_VR", StringComparison.Ordinal))
                        Interlocked.Increment(ref _androidVrSessionBotDetections);

                    errors.Add($"{clientName}: BOT_DETECTION (LOGIN_REQUIRED)");
                    // allBotDetection остаётся true
                }
                else
                {
                    if (!hasLoginRequired)
                    {
                        hasLoginRequired = true;
                        loginException = ex;
                    }
                    errors.Add($"{clientName}: LOGIN_REQUIRED ({ex.Reason})");
                    allBotDetection = false;
                }
                hasNonNetworkFailure = true;
            }
            catch (YoutubeNetworkException netEx)
            {
                Log.Warn($"[VideoController] [{videoId}] {clientName} network error: {netEx.ErrorType} — {netEx.Message}");
                errors.Add($"{clientName}: NETWORK_{netEx.ErrorType}");
                firstNetworkException ??= netEx;
                allBotDetection = false;
            }
            catch (StreamUnavailableException) { throw; }
            catch (BotDetectionException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Пытаемся классифицировать как сетевую ошибку (на случай
                // если YoutubeHttpHandler не обернул — например, ошибка до SendAsync)
                var classified = YoutubeNetworkException.TryClassify(ex, cancellationToken);
                if (classified is not null)
                {
                    Log.Warn($"[VideoController] [{videoId}] {clientName} classified as network: {classified.ErrorType} — {ex.Message}");
                    errors.Add($"{clientName}: NETWORK_{classified.ErrorType}");
                    firstNetworkException ??= classified;
                }
                else
                {
                    Log.Warn($"[VideoController] [{videoId}] {clientName} exception: {ex.Message}");
                    errors.Add($"{clientName}: {ex.Message}");
                    hasNonNetworkFailure = true;
                }
                allBotDetection = false;
            }
        }

        // Приоритет финальных ошибок

        // 1. Все клиенты упали из-за сети — пользователь должен увидеть "проблема с сетью"
        if (firstNetworkException is not null && !hasNonNetworkFailure)
        {
            Log.Error($"[VideoController] [{videoId}] All clients failed due to network: {firstNetworkException.ErrorType}");
            throw firstNetworkException;
        }

        // 2. Все клиенты потребовали логин (не бот)
        if (hasLoginRequired && loginException != null)
        {
            Log.Error($"[VideoController] [{videoId}] All clients require login: {loginException.Reason}");
            throw loginException;
        }

        var allErrors = string.Join("; ", errors);

        // 3. Все клиенты заблокированы ботодетекцией
        if (allBotDetection)
        {
            throw new BotDetectionException(
                $"All clients detected as bot for {videoId}",
                GetRemainingCooldown());
        }

        // 4. Смешанные ошибки, но есть сетевая — упоминаем в сообщении
        if (firstNetworkException is not null)
        {
            Log.Error($"[VideoController] [{videoId}] Mixed failures (including network: {firstNetworkException.ErrorType}). Errors: {allErrors}");
            throw firstNetworkException;
        }

        throw new VideoUnplayableException(
            $"Video {videoId} is not available through any client. Errors: {allErrors}",
            videoId.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAnyStream(PlayerResponse response)
    {
        foreach (var _ in response.Streams) return true;
        return false;
    }

    public async ValueTask<string?> GetHlsManifestUrlAsync(
        VideoId videoId,
        CancellationToken cancellationToken = default)
    {
        foreach (var clientName in YoutubeClientUtils.HlsFallbackClients)
        {
            try
            {
                var response = await GetPlayerResponseWithClientAsync(videoId, clientName, cancellationToken);

                var hlsUrl = response.HlsManifestUrl;
                if (!string.IsNullOrEmpty(hlsUrl))
                {
                    Log.Info($"[VideoController] [{videoId}] HLS found via {clientName}");
                    return hlsUrl;
                }
            }
            catch (StreamUnavailableException ex) when (ex.HttpStatusCode == 403)
            {
                Log.Warn($"[VideoController] [{videoId}] HLS via {clientName} got 403");

                throw new StreamUnavailableException(
                    $"HLS stream returned 403 for video {videoId}",
                    videoId.Value,
                    StreamUnavailableReason.Forbidden403,
                    httpStatusCode: 403,
                    wasHlsFallback: true);
            }
            catch (BotDetectionException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"[VideoController] [{videoId}] HLS via {clientName} failed: {ex.Message}");
            }
        }

        Log.Warn($"[VideoController] [{videoId}] No HLS manifest available from any client");
        return null;
    }

    public async ValueTask<PlayerResponse> GetPlayerResponseAsync(
        VideoId videoId,
        string? signatureTimestamp,
        CancellationToken cancellationToken = default)
    {
        return await GetPlayerResponseWithClientAsync(
            videoId, "TVHTML5_SIMPLY_EMBEDDED_PLAYER", cancellationToken, signatureTimestamp);
    }

    public async ValueTask<DashManifest> GetDashManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
        => DashManifest.Parse(await Http.GetStringAsync(url, cancellationToken));

    /// <summary>
    /// Сбрасывает закэшированный signatureTimestamp через <see cref="PlayerContextManager"/>.
    /// Затрагивает все экземпляры <see cref="VideoController"/>, использующие тот же менеджер.
    /// </summary>
    internal void InvalidateSignatureTimestamp()
    {
        _playerManager.InvalidateSignatureTimestamp();
        Log.Debug("[VideoController] SignatureTimestamp invalidated via PlayerContextManager");
    }

    #endregion
}